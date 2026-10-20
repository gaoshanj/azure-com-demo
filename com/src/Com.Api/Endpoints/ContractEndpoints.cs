using System.Security.Claims;
using System.Text.Json;
using Com.Api.Services;
using Com.Shared.Abstractions;
using Com.Shared.Contracts;
using Com.Shared.Data;
using Com.Shared.Domain;
using Com.Shared.Workers;
using Microsoft.EntityFrameworkCore;

namespace Com.Api.Endpoints;

public static class ContractEndpoints
{
    public static void MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        // 生成合同：只投递任务，不等渲染完成（同步链路在此截断）
        app.MapPost("/api/contracts/{orderId:guid}/generate", async (Guid orderId, ClaimsPrincipal p,
            ComDbContext db, IJobQueue jobs) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId)
                        ?? throw new KeyNotFoundException("订单不存在");
            if (order.Status != OrderStatus.Approved)
                throw new InvalidTransitionException(order.Status, "generate");

            var lastVersion = await db.Contracts.Where(c => c.OrderId == orderId)
                .Select(c => (int?)c.Version).MaxAsync() ?? 0;

            var contract = new Contract
            {
                OrderId = orderId,
                Version = lastVersion + 1,
                Status = ContractStatus.Generating,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Contracts.Add(contract);

            order.Status = OrderStateMachine.Next(order.Status, "generate");
            order.Version++;
            db.AuditEvents.Add(new AuditEvent
            {
                EntityType = "Order", EntityId = orderId.ToString(), Action = "contract.requested",
                FromStatus = "Approved", ToStatus = order.Status.ToString(), Actor = p.Upn(),
                TraceId = System.Diagnostics.Activity.Current?.Id ?? "local", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            await jobs.EnqueueAsync(new JobMessage(JobTypes.ContractRender, contract.Id,
                new Dictionary<string, string> { ["orderNo"] = order.OrderNo }));

            return Results.Accepted($"/api/orders/{orderId}", new { contractId = contract.Id, version = contract.Version });
        }).RequireAuthorization();

        // 下载：优先让浏览器直连 SAS；本地/无 SAS 时由 API 流式下发
        app.MapGet("/api/contracts/{contractId:guid}/download", async (Guid contractId, ComDbContext db, IBlobStore blobs) =>
        {
            var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId);
            if (c?.BlobPath == null) return Results.NotFound();

            var content = await blobs.OpenReadAsync(c.BlobContainer, c.BlobPath);
            if (content == null) return Results.NotFound();

            var orderNo = await db.Orders.Where(o => o.Id == c.OrderId).Select(o => o.OrderNo).FirstOrDefaultAsync();
            return Results.File(content.Stream, "application/pdf", $"{orderNo}-contract-v{c.Version}.pdf");
        }).RequireAuthorization();

        // 校验：重算 SHA256，证明文件未被替换（WORM + 摘要的双保险）
        app.MapGet("/api/contracts/{contractId:guid}/verify", async (Guid contractId, ComDbContext db, IBlobStore blobs) =>
        {
            var c = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId);
            if (c?.BlobPath == null) return Results.NotFound();

            var content = await blobs.OpenReadAsync(c.BlobContainer, c.BlobPath);
            if (content == null) return Results.NotFound();

            await using (content)
            {
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Stream)).ToLowerInvariant();
                return Results.Ok(new
                {
                    stored = c.Sha256,
                    recomputed = sha,
                    match = string.Equals(sha, c.Sha256, StringComparison.OrdinalIgnoreCase),
                    sizeBytes = content.Size,
                    immutableUntil = c.ImmutableUntil
                });
            }
        }).RequireAuthorization();
    }
}

public static class IntakeEndpoints
{
    public static void MapIntakeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/intake/upload", async (HttpRequest request, ClaimsPrincipal p, ComDbContext db,
            IBlobStore blobs, IJobQueue jobs) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "需要 multipart/form-data" });
            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Results.BadRequest(new { message = "未收到文件" });
            if (file.Length > 20 * 1024 * 1024) return Results.BadRequest(new { message = "文件不能超过 20MB" });

            var ext = Path.GetExtension(file.FileName);
            var doc = new IntakeDocument
            {
                FileName = file.FileName,
                UploadedBy = p.Upn(),
                Status = IntakeStatus.Queued,
                BlobContainer = "intake",
                BlobPath = $"{DateTimeOffset.UtcNow:yyyyMM}/{Guid.NewGuid():N}{ext}",
                CreatedAt = DateTimeOffset.UtcNow
            };

            await using (var s = file.OpenReadStream())
                await blobs.PutAsync(doc.BlobContainer, doc.BlobPath, s, file.ContentType ?? "application/octet-stream");

            db.IntakeDocuments.Add(doc);
            await db.SaveChangesAsync();

            await jobs.EnqueueAsync(new JobMessage(JobTypes.IntakeOcr, doc.Id));

            return Results.Accepted($"/api/intake/{doc.Id}",
                new IntakeUploadResponse(doc.Id, doc.FileName, doc.Status.ToString()));
        }).RequireAuthorization().DisableAntiforgery();

        app.MapGet("/api/intake/{id:guid}", async (Guid id, ComDbContext db) =>
        {
            var doc = await db.IntakeDocuments.FirstOrDefaultAsync(d => d.Id == id);
            if (doc == null) return Results.NotFound();
            return Results.Ok(new IntakeDto(doc.Id, doc.FileName, doc.Status.ToString(), doc.Confidence,
                doc.DraftOrderId, IntakeWorkHandler.Parse(doc.ExtractedJson), doc.CreatedAt, doc.Error));
        }).RequireAuthorization();

        app.MapGet("/api/intake", async (ComDbContext db) =>
        {
            var docs = await db.IntakeDocuments.OrderByDescending(d => d.CreatedAt).Take(20).ToListAsync();
            return Results.Ok(docs.Select(d => new IntakeDto(d.Id, d.FileName, d.Status.ToString(), d.Confidence,
                d.DraftOrderId, IntakeWorkHandler.Parse(d.ExtractedJson), d.CreatedAt, d.Error)));
        }).RequireAuthorization();
    }
}

public static class OpsEndpoints
{
    public static void MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ops/deps", async (ComDbContext db, IBlobStore blobs, IJobQueue jobs,
            IAuditMirror mirror, IPdfRenderer pdf, ICacheStore cache) =>
        {
            var list = new List<DepStatusDto>();

            async Task Probe(string name, string kind, Func<Task<bool>> action)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok;
                string detail;
                try { ok = await action(); detail = ok ? "正常" : "不可用"; }
                catch (Exception ex) { ok = false; detail = ex.Message.Length > 160 ? ex.Message[..160] : ex.Message; }
                sw.Stop();
                list.Add(new DepStatusDto(name, kind, ok, sw.ElapsedMilliseconds, detail));
            }

            await Probe("Azure SQL / SQLite", "关系型数据库", async () =>
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1");
                return true;
            });
            await Probe(blobs.Mode == "azure" ? "Blob Storage" : "本地文件存储", "对象存储", () => blobs.PingAsync());
            await Probe(jobs.Mode == "servicebus" ? "Service Bus" : "进程内队列", "消息", () => jobs.PingAsync());
            await Probe("Cosmos DB（审计镜像）", "NoSQL", () => mirror.PingAsync());
            await Probe("PDF 渲染（含中文字体）", "计算", () => Task.FromResult(pdf.Ping()));
            await Probe("Cache", "缓存", () => cache.PingAsync());

            return Results.Ok(list);
        }).RequireAuthorization();

        app.MapGet("/api/ops/summary", async (ClaimsPrincipal p, ComDbContext db, IConfiguration cfg,
            IBlobStore blobs, IJobQueue jobs, IWorkflowBridge wf, IAuditMirror mirror, IPdfRenderer pdf,
            ICacheStore cache, IHostEnvironment env) =>
        {
            var deps = new List<DepStatusDto>();
            async Task Probe(string name, string kind, Func<Task<bool>> action)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok; string detail;
                try { ok = await action(); detail = ok ? "正常" : "不可用"; }
                catch (Exception ex) { ok = false; detail = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message; }
                sw.Stop();
                deps.Add(new DepStatusDto(name, kind, ok, sw.ElapsedMilliseconds, detail));
            }
            await Probe("数据库", "Data", async () => { await db.Database.ExecuteSqlRawAsync("SELECT 1"); return true; });
            await Probe("对象存储", "Storage", () => blobs.PingAsync());
            await Probe("消息队列", "Integration", () => jobs.PingAsync());
            await Probe("审计镜像", "NoSQL", () => mirror.PingAsync());
            await Probe("PDF 引擎", "Compute", () => Task.FromResult(pdf.Ping()));
            await Probe("缓存", "Cache", () => cache.PingAsync());

            var summary = new OpsSummaryDto(
                cfg["ENVIRONMENT_NAME"] ?? env.EnvironmentName,
                cfg["AZURE_REGION"] ?? "local",
                cfg["APP_VERSION"] ?? "1.0.0",
                await db.Orders.CountAsync(),
                await db.ApprovalTasks.CountAsync(t => t.Decision == ApprovalDecision.Pending),
                await db.Contracts.CountAsync(c => c.Status == ContractStatus.Archived),
                deps,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>
                {
                    ["storage"] = blobs.Mode,
                    ["queue"] = jobs.Mode,
                    ["workflow"] = wf.Mode,
                    ["audit"] = mirror.Mode,
                    ["cache"] = cache.Mode,
                    ["db"] = cfg["DB_PROVIDER"] ?? "sqlite"
                });

            return Results.Ok(summary);
        }).RequireAuthorization();
    }
}

// ─────────────────────────────────────────────────────────────
// 内部端点：只允许编排服务（Logic Apps）调用，共享密钥鉴权
// ─────────────────────────────────────────────────────────────

// 工作流所有回调的载荷都是「触发器原始 body」，回调地址通过 x-callback-url 头传递，
// 这样 Logic Apps 侧不需要在字符串里嵌套引号，定义更干净。
public record InternalNotifyRequest(Guid TaskId, Guid OrderId, int Level, string ApproverUpn, decimal Amount);

public static class InternalEndpoints
{
    private static bool KeyOk(HttpContext ctx, IConfiguration cfg)
    {
        var expected = cfg["INTERNAL_KEY"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var got = ctx.Request.Headers["x-internal-key"].FirstOrDefault();
        return !string.IsNullOrEmpty(got) && string.Equals(got, expected, StringComparison.Ordinal);
    }

    public static void MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        // 工作流第 1 步：通知审批人
        app.MapPost("/internal/notify", async (InternalNotifyRequest req, HttpContext ctx, IConfiguration cfg,
            ComDbContext db, INotifier notifier) =>
        {
            if (!KeyOk(ctx, cfg)) return Results.Unauthorized();

            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == req.OrderId);
            var task = await db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == req.TaskId);
            if (order == null || task == null) return Results.NotFound();

            var customer = await db.Customers.Where(c => c.Id == order.CustomerId).Select(c => c.Name).FirstOrDefaultAsync();
            await notifier.SendAsync(req.ApproverUpn, $"待审批 · {order.OrderNo}（L{req.Level}）",
                $"客户 {customer} 提交订单 ¥{req.Amount:N2}，{ApprovalRules.Describe(req.Amount)}，请在 48 小时内处理。",
                $"/#/orders/{order.Id}");
            return Results.Ok(new { ok = true });
        });

        // 工作流第 2 步：注册回调地址（等价于凭据，仅服务端保存）
        app.MapPost("/internal/webhook/subscribe", async (InternalNotifyRequest req, HttpContext ctx,
            IConfiguration cfg, ComDbContext db) =>
        {
            if (!KeyOk(ctx, cfg)) return Results.Unauthorized();
            var callbackUrl = ctx.Request.Headers["x-callback-url"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(callbackUrl)) return Results.BadRequest(new { message = "缺少 x-callback-url 头" });
            var task = await db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == req.TaskId);
            if (task == null) return Results.NotFound();
            task.CallbackUrl = callbackUrl;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        // 工作流超时/取消：清理回调地址
        app.MapPost("/internal/webhook/unsubscribe", async (InternalNotifyRequest req, HttpContext ctx,
            IConfiguration cfg, ComDbContext db) =>
        {
            if (!KeyOk(ctx, cfg)) return Results.Unauthorized();
            var task = await db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == req.TaskId);
            if (task != null) { task.CallbackUrl = null; await db.SaveChangesAsync(); }
            return Results.Ok(new { ok = true });
        });

        // 工作流恢复后：推进到下一级（决策以数据库为准，工作流只负责"推进"）
        app.MapPost("/internal/workflow/level-complete", async (InternalNotifyRequest req, HttpContext ctx,
            IConfiguration cfg, ComDbContext db, ApprovalService svc) =>
        {
            if (!KeyOk(ctx, cfg)) return Results.Unauthorized();
            var task = await db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == req.TaskId);
            if (task == null) return Results.NotFound();
            await svc.AdvanceAsync(task.OrderId, "workflow");
            return Results.Ok(new { ok = true });
        });
    }
}

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", async (ClaimsPrincipal p, ComDbContext db) =>
            Results.Ok(await db.Notifications.Where(n => n.UserUpn == p.Upn())
                .OrderByDescending(n => n.CreatedAt).Take(30)
                .Select(n => new { n.Id, n.Title, n.Body, n.Link, n.Read, n.CreatedAt }).ToListAsync()))
            .RequireAuthorization();

        app.MapPost("/api/notifications/read", async (ClaimsPrincipal p, ComDbContext db) =>
        {
            var list = await db.Notifications.Where(n => n.UserUpn == p.Upn() && !n.Read).ToListAsync();
            list.ForEach(n => n.Read = true);
            await db.SaveChangesAsync();
            return Results.Ok(new { updated = list.Count });
        }).RequireAuthorization();
    }
}
