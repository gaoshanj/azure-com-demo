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

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // ── 列表 ─────────────────────────────────────────────
        app.MapGet("/api/orders", async (ClaimsPrincipal p, ComDbContext db, string? status, string? q) =>
        {
            var upn = p.Upn();
            var query = db.Orders.Include(o => o.Lines).AsQueryable();

            if (p.Role() == Roles.Sales) query = query.Where(o => o.CreatedBy == upn);
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, true, out var st))
                query = query.Where(o => o.Status == st);

            var orders = await query.OrderByDescending(o => o.CreatedAt).Take(300).ToListAsync();
            var ids = orders.Select(o => o.Id).ToList();

            var stats = await db.ApprovalTasks.Where(t => ids.Contains(t.OrderId))
                .GroupBy(t => t.OrderId)
                .Select(g => new
                {
                    OrderId = g.Key,
                    Approved = g.Count(t => t.Decision == ApprovalDecision.Approved),
                    Pending = g.Count(t => t.Decision == ApprovalDecision.Pending)
                }).ToListAsync();

            var customers = await db.Customers.ToDictionaryAsync(c => c.Id, c => c.Name);

            var list = orders.Select(o =>
            {
                var s = stats.FirstOrDefault(x => x.OrderId == o.Id);
                var maxLevel = ApprovalRules.MaxLevel(o.TotalAmount);
                var stage = o.Status switch
                {
                    OrderStatus.PendingApproval => $"L{(s?.Approved ?? 0) + 1}/{maxLevel} 审批中",
                    OrderStatus.Approved => $"{maxLevel} 级已通过",
                    OrderStatus.Contracted or OrderStatus.Archived => "合同已生成",
                    _ => $"{maxLevel} 级审批"
                };
                return new OrderListItemDto(o.Id, o.OrderNo, customers.GetValueOrDefault(o.CustomerId, "—"),
                    o.Status.ToString(), OrderStateMachine.Label(o.Status), o.TotalAmount, o.Currency,
                    o.CreatedAt, stage, o.CreatedBy);
            }).ToList();

            if (!string.IsNullOrWhiteSpace(q))
                list = list.Where(o => o.OrderNo.Contains(q, StringComparison.OrdinalIgnoreCase)
                                    || o.CustomerName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            return Results.Ok(list);
        }).RequireAuthorization();

        // ── 详情 ─────────────────────────────────────────────
        app.MapGet("/api/orders/{id:guid}", async (Guid id, ClaimsPrincipal p, ComDbContext db, IBlobStore blobs) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return Results.NotFound();
            if (p.Role() == Roles.Sales && order.CreatedBy != p.Upn()) return Results.NotFound();
            return Results.Ok(await DetailAsync(db, order, p, blobs));
        }).RequireAuthorization();

        // ── 新建 ─────────────────────────────────────────────
        app.MapPost("/api/orders", async (CreateOrderRequest req, ClaimsPrincipal p, ComDbContext db, IBlobStore blobs) =>
        {
            if (req.Lines == null || req.Lines.Count == 0)
                throw new ValidationProblemException("订单至少需要一行明细", new { lines = new[] { "明细不能为空" } });

            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                var dup = await db.Orders.Include(o => o.Lines)
                    .FirstOrDefaultAsync(o => o.IdempotencyKey == req.IdempotencyKey);
                if (dup != null) return Results.Ok(await DetailAsync(db, dup, p, blobs));  // 幂等：重复请求返回首次结果
            }

            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == req.CustomerId)
                           ?? throw new KeyNotFoundException("客户不存在");
            var products = await db.Products.ToDictionaryAsync(x => x.Id);

            var order = new Order
            {
                OrderNo = await OrderNumbering.NextAsync(db),
                CustomerId = customer.Id,
                Status = OrderStatus.Draft,
                Currency = "CNY",
                Terms = string.IsNullOrWhiteSpace(req.Terms) ? "款到发货" : req.Terms!,
                Remark = req.Remark,
                CreatedBy = p.Upn(),
                CreatedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = req.IdempotencyKey
            };

            var errors = new Dictionary<string, List<string>>();
            var no = 1;
            foreach (var l in req.Lines)
            {
                if (!products.TryGetValue(l.ProductId, out var prod))
                {
                    errors.TryAdd("lines", new List<string>());
                    errors["lines"].Add($"产品 {l.ProductId} 不存在");
                    continue;
                }
                if (l.Qty <= 0)
                {
                    errors.TryAdd("lines", new List<string>());
                    errors["lines"].Add($"{prod.Name} 数量必须大于 0");
                    continue;
                }
                var unit = l.UnitPrice ?? prod.UnitPrice;
                order.Lines.Add(new OrderLine
                {
                    OrderId = order.Id, LineNo = no++, ProductId = prod.Id, ProductName = prod.Name,
                    Qty = l.Qty, UnitPrice = unit, Amount = unit * l.Qty
                });
            }
            if (errors.Count > 0) throw new ValidationProblemException("订单明细校验失败", errors);

            order.TotalAmount = order.Lines.Sum(x => x.Amount);
            db.Orders.Add(order);
            db.AuditEvents.Add(new AuditEvent
            {
                EntityType = "Order", EntityId = order.Id.ToString(), Action = "order.created",
                ToStatus = OrderStatus.Draft.ToString(), Actor = p.Upn(),
                TraceId = System.Diagnostics.Activity.Current?.Id ?? "local",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            return Results.Created($"/api/orders/{order.Id}", await DetailAsync(db, order, p, blobs));
        }).RequireAuthorization();

        // ── 提交 ─────────────────────────────────────────────
        app.MapPost("/api/orders/{id:guid}/submit", async (Guid id, ClaimsPrincipal p, ComDbContext db, ApprovalService svc, IBlobStore blobs) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id)
                        ?? throw new KeyNotFoundException("订单不存在");
            if (order.CreatedBy != p.Upn() && p.Role() != Roles.Admin)
                throw new UnauthorizedAccessException("只能提交自己创建的订单");
            await svc.SubmitAsync(order, p.Upn());
            return Results.Ok(await DetailAsync(db, order, p, blobs));
        }).RequireAuthorization();

        // ── 取消 ─────────────────────────────────────────────
        app.MapPost("/api/orders/{id:guid}/cancel", async (Guid id, ClaimsPrincipal p, ComDbContext db, IBlobStore blobs) =>
        {
            var order = await db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id)
                        ?? throw new KeyNotFoundException("订单不存在");
            if (order.CreatedBy != p.Upn() && p.Role() != Roles.Admin)
                throw new UnauthorizedAccessException("只能取消自己创建的订单");

            var from = order.Status;
            order.Status = OrderStateMachine.Next(order.Status, "cancel");
            order.Version++;
            db.AuditEvents.Add(new AuditEvent
            {
                EntityType = "Order", EntityId = order.Id.ToString(), Action = "order.cancelled",
                FromStatus = from.ToString(), ToStatus = order.Status.ToString(), Actor = p.Upn(),
                TraceId = System.Diagnostics.Activity.Current?.Id ?? "local", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return Results.Ok(await DetailAsync(db, order, p, blobs));
        }).RequireAuthorization();

        // ── 时间线 ───────────────────────────────────────────
        app.MapGet("/api/orders/{id:guid}/timeline", async (Guid id, ClaimsPrincipal p, ComDbContext db) =>
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id) ?? throw new KeyNotFoundException("订单不存在");
            if (p.Role() == Roles.Sales && order.CreatedBy != p.Upn()) throw new UnauthorizedAccessException("无权查看");

            var events = await db.AuditEvents.Where(e => e.EntityType == "Order" && e.EntityId == id.ToString())
                .OrderBy(e => e.CreatedAt).ToListAsync();
            return Results.Ok(events.Select(e => new TimelineItemDto(
                e.Action, e.FromStatus, e.ToStatus, e.Actor, e.CreatedAt, e.TraceId)));
        }).RequireAuthorization();
    }

    internal static async Task<OrderDetailDto> DetailAsync(ComDbContext db, Order order, ClaimsPrincipal p, IBlobStore blobs)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == order.CustomerId);
        var tasks = await db.ApprovalTasks.Where(t => t.OrderId == order.Id).OrderBy(t => t.Level).ToListAsync();
        var contract = await db.Contracts.Where(c => c.OrderId == order.Id)
            .OrderByDescending(c => c.Version).FirstOrDefaultAsync();

        ContractDto? contractDto = null;
        if (contract != null)
        {
            string? sas = null;
            if (contract.Status == ContractStatus.Archived && !string.IsNullOrWhiteSpace(contract.BlobPath))
                sas = await blobs.GetSasUrlAsync(contract.BlobContainer, contract.BlobPath, TimeSpan.FromMinutes(5));
            contractDto = new ContractDto(contract.Id, contract.Status.ToString(), contract.Sha256, contract.SizeBytes,
                contract.Version, contract.BlobPath, contract.ImmutableUntil, contract.CreatedAt, contract.Error, sas);
        }

        var maxLevel = ApprovalRules.MaxLevel(order.TotalAmount);
        var now = DateTimeOffset.UtcNow;
        var steps = Enumerable.Range(1, maxLevel).Select(level =>
        {
            var t = tasks.FirstOrDefault(x => x.Level == level);
            var role = ApprovalRules.Chain(order.TotalAmount)[level - 1];
            return new ApprovalStepDto(level, role, Roles.Label(role), t?.ApproverUpn ?? "",
                t?.Decision.ToString() ?? "NotStarted", t?.Comment, t?.DecidedAt,
                t?.DueAt ?? now.AddHours(48),
                t != null && t.Decision == ApprovalDecision.Pending && t.DueAt < now);
        }).ToList();

        var myPending = tasks.Any(t => t.Decision == ApprovalDecision.Pending
                                       && t.ApproverUpn.Equals(p.Upn(), StringComparison.OrdinalIgnoreCase));

        return new OrderDetailDto(
            order.Id, order.OrderNo, order.CustomerId, customer?.Name ?? "—", customer?.Code ?? "—",
            order.Status.ToString(), OrderStateMachine.Label(order.Status), order.TotalAmount, order.Currency,
            order.Terms, order.Remark, order.CreatedBy, order.CreatedAt, order.SubmittedAt, order.Version,
            ApprovalRules.Describe(order.TotalAmount),
            order.Lines.OrderBy(l => l.LineNo)
                .Select(l => new OrderLineDto(l.LineNo, l.ProductName, l.Qty, l.UnitPrice, l.Amount)).ToList(),
            steps, contractDto,
            CanSubmit: order.Status == OrderStatus.Draft && (order.CreatedBy == p.Upn() || p.Role() == Roles.Admin),
            CanApprove: myPending);
    }
}

public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/approvals/pending", async (ClaimsPrincipal p, ComDbContext db) =>
        {
            var upn = p.Upn();
            var tasks = await db.ApprovalTasks
                .Where(t => t.ApproverUpn == upn && t.Decision == ApprovalDecision.Pending)
                .OrderBy(t => t.DueAt).ToListAsync();

            var orderIds = tasks.Select(t => t.OrderId).ToList();
            var orders = await db.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
            var customers = await db.Customers.ToDictionaryAsync(c => c.Id, c => c.Name);
            var now = DateTimeOffset.UtcNow;

            var list = tasks.Where(t => orders.ContainsKey(t.OrderId)).Select(t =>
            {
                var o = orders[t.OrderId];
                return new PendingApprovalDto(t.Id, o.Id, o.OrderNo, customers.GetValueOrDefault(o.CustomerId, "—"),
                    o.TotalAmount, o.Currency, t.Level, ApprovalRules.MaxLevel(o.TotalAmount),
                    t.DueAt, t.CreatedAt, t.DueAt < now, ApprovalRules.Describe(o.TotalAmount));
            }).ToList();

            return Results.Ok(list);
        }).RequireAuthorization();

        app.MapGet("/api/approvals/summary", async (ClaimsPrincipal p, ComDbContext db) =>
        {
            var upn = p.Upn();
            // SQLite 对 DateTimeOffset 的比较支持有限，统计放在内存里算（数据量极小）
            var mine = await db.ApprovalTasks.AsNoTracking().Where(t => t.ApproverUpn == upn).ToListAsync();
            var now = DateTimeOffset.UtcNow;
            var pending = mine.Count(t => t.Decision == ApprovalDecision.Pending);
            var overdue = mine.Count(t => t.Decision == ApprovalDecision.Pending && t.DueAt < now);
            var decided7d = mine.Count(t => t.Decision != ApprovalDecision.Pending
                                            && t.DecidedAt.HasValue && t.DecidedAt.Value > now.AddDays(-7));
            return Results.Ok(new { pending, overdue, decided7d });
        }).RequireAuthorization();

        app.MapPost("/api/approvals/{taskId:guid}/decision", async (Guid taskId, DecisionRequest req,
            ClaimsPrincipal p, ApprovalService svc) =>
        {
            var result = await svc.DecideAsync(taskId, req.Decision ?? "approve", req.Comment, p.Upn());
            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapPost("/api/approvals/batch", async (BatchDecisionRequest req, ClaimsPrincipal p, ApprovalService svc) =>
        {
            var ok = 0;
            var failed = new List<object>();
            foreach (var id in req.TaskIds ?? new List<Guid>())
            {
                try { await svc.DecideAsync(id, req.Decision ?? "approve", req.Comment, p.Upn()); ok++; }
                catch (Exception ex) { failed.Add(new { taskId = id, error = ex.Message }); }
            }
            return Results.Ok(new { succeeded = ok, failed });
        }).RequireAuthorization();
    }
}
