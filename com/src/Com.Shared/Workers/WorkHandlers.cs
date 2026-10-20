using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Com.Shared.Abstractions;
using Com.Shared.Contracts;
using Com.Shared.Data;
using Com.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Com.Shared.Workers;

public static class OrderNumbering
{
    /// <summary>形如 COM-20261020-0007。日序号 + 唯一索引兜底。</summary>
    public static async Task<string> NextAsync(ComDbContext db, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var prefix = $"COM-{now:yyyyMMdd}-";
        var todayCount = await db.Orders.CountAsync(o => o.OrderNo.StartsWith(prefix), ct);
        for (var i = 1; i <= 999; i++)
        {
            var candidate = prefix + (todayCount + i).ToString("D4");
            if (!await db.Orders.AnyAsync(o => o.OrderNo == candidate, ct)) return candidate;
        }
        return prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
    }
}

/// <summary>
/// 合同渲染 + 归档。这个类与宿主无关：
/// 既可以被 Service Bus 触发的 Function 调用，也可以在 API 内联执行（演示兜底）。
/// </summary>
public class ContractWorkHandler
{
    private readonly ComDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly IPdfRenderer _pdf;
    private readonly INotifier _notifier;
    private readonly IAuditMirror _mirror;
    private readonly ILogger<ContractWorkHandler> _log;

    public ContractWorkHandler(ComDbContext db, IBlobStore blobs, IPdfRenderer pdf,
                               INotifier notifier, IAuditMirror mirror, ILogger<ContractWorkHandler> log)
    {
        _db = db; _blobs = blobs; _pdf = pdf; _notifier = notifier; _mirror = mirror; _log = log;
    }

    public async Task HandleAsync(Guid contractId, CancellationToken ct = default)
    {
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == contractId, ct)
                       ?? throw new InvalidOperationException($"合同记录不存在：{contractId}");

        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == contract.OrderId, ct)
                    ?? throw new InvalidOperationException($"订单不存在：{contract.OrderId}");
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == order.CustomerId, ct);
        var approvals = await _db.ApprovalTasks.Where(a => a.OrderId == order.Id)
                            .OrderBy(a => a.Level).ToListAsync(ct);

        try
        {
            var model = new ContractRenderModel(
                order.OrderNo,
                customer?.Name ?? "—",
                customer?.Code ?? "—",
                customer?.TaxId ?? "—",
                order.TotalAmount,
                order.Currency,
                order.Terms,
                order.CreatedBy,
                order.CreatedAt,
                order.Remark,
                order.Lines.OrderBy(l => l.LineNo)
                     .Select(l => new ContractLine(l.LineNo, l.ProductName, l.Qty, l.UnitPrice, l.Amount)).ToList(),
                approvals.Select(a =>
                    $"L{a.Level} {Roles.Label(a.ApproverRole)} {a.ApproverUpn} · {(a.Decision == ApprovalDecision.Approved ? "批准" : a.Decision.ToString())} · " +
                    $"{(a.DecidedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm") ?? "—")}" +
                    (string.IsNullOrWhiteSpace(a.Comment) ? "" : $" · {a.Comment}")).ToList());

            var bytes = _pdf.Render(model);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var path = $"{order.OrderNo}/v{contract.Version}-{contract.Id:N}.pdf";

            var blobRef = await _blobs.PutAsync("contracts", path, new MemoryStream(bytes), "application/pdf", ct);

            contract.BlobPath = path;
            contract.Sha256 = sha;
            contract.SizeBytes = bytes.Length;
            contract.Status = ContractStatus.Archived;
            contract.GeneratedBy = "worker";
            contract.Error = null;
            contract.ImmutableUntil = blobRef.ImmutableUntil
                                      ?? DateTimeOffset.UtcNow.AddDays(30);

            if (order.Status == OrderStatus.Contracted)
            {
                order.Status = OrderStateMachine.Next(order.Status, "archive");
                order.Version++;
            }

            var evt = new AuditEvent
            {
                EntityType = "Order",
                EntityId = order.Id.ToString(),
                Action = "contract.archived",
                FromStatus = OrderStatus.Contracted.ToString(),
                ToStatus = OrderStatus.Archived.ToString(),
                Actor = "worker",
                Payload = JsonSerializer.Serialize(new { path, sha256 = sha, size = bytes.Length }),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.AuditEvents.Add(evt);

            await _db.SaveChangesAsync(ct);
            _ = _mirror.WriteAsync(evt, ct);

            await _notifier.SendAsync(order.CreatedBy,
                $"合同已归档 · {order.OrderNo}",
                $"合同 PDF 已写入不可变存储，SHA256 {sha[..16]}…，保留至 {contract.ImmutableUntil:yyyy-MM-dd}。",
                $"/#/orders/{order.Id}", ct);

            _log.LogInformation("合同归档完成 {OrderNo} size={Size} sha={Sha}", order.OrderNo, bytes.Length, sha);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "合同渲染失败 {ContractId}", contractId);
            contract.Status = ContractStatus.Failed;
            contract.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            _db.AuditEvents.Add(new AuditEvent
            {
                EntityType = "Order",
                EntityId = order.Id.ToString(),
                Action = "contract.failed",
                Actor = "worker",
                Payload = JsonSerializer.Serialize(new { error = contract.Error }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>
/// 扫描件字段抽取。PoC 用确定性模拟实现（同文件同结果），
/// 生产替换为 Azure AI Document Intelligence —— 接口不变，只换实现。
/// </summary>
public class IntakeWorkHandler
{
    private readonly ComDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly INotifier _notifier;
    private readonly ILogger<IntakeWorkHandler> _log;

    public IntakeWorkHandler(ComDbContext db, IBlobStore blobs, INotifier notifier, ILogger<IntakeWorkHandler> log)
    {
        _db = db; _blobs = blobs; _notifier = notifier; _log = log;
    }

    public async Task HandleAsync(Guid intakeId, CancellationToken ct = default)
    {
        var doc = await _db.IntakeDocuments.FirstOrDefaultAsync(d => d.Id == intakeId, ct);
        if (doc == null) return;

        try
        {
            doc.Status = IntakeStatus.Extracting;
            await _db.SaveChangesAsync(ct);

            byte[] bytes;
            await using (var content = await _blobs.OpenReadAsync(doc.BlobContainer, doc.BlobPath, ct))
            {
                if (content == null) throw new InvalidOperationException("原始文件不存在");
                using var ms = new MemoryStream();
                await content.Stream.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            var seed = BitConverter.ToInt32(SHA256.HashData(bytes), 0) & 0x7FFFFFFF;
            var rnd = new Random(seed);

            var customers = await _db.Customers.OrderBy(c => c.Code).ToListAsync(ct);
            var products = await _db.Products.Where(p => p.Active).OrderBy(p => p.Sku).ToListAsync(ct);
            var customer = customers[rnd.Next(customers.Count)];

            var fields = new List<IntakeExtractedField>
            {
                new("customer", "客户名称", customer.Name, 0.94, "mock-ocr"),
                new("taxId", "纳税人识别号", customer.TaxId, 0.91, "mock-ocr"),
                new("terms", "付款条款", rnd.Next(2) == 0 ? "款到发货" : "月结 30 天", 0.96, "mock-ocr"),
                new("signDate", "签署日期", DateTimeOffset.UtcNow.AddDays(-rnd.Next(1, 20)).ToString("yyyy-MM-dd"), 0.72, "mock-ocr"),
                new("supplier", "供方签章", "格思信息技术有限公司", 0.87, "mock-ocr"),
            };

            // 生成一张草稿订单，方便演示"抽取 → 建单"
            var order = new Order
            {
                OrderNo = await OrderNumbering.NextAsync(_db, ct),
                CustomerId = customer.Id,
                Status = OrderStatus.Draft,
                Currency = "CNY",
                Terms = fields.First(f => f.Field == "terms").Value,
                CreatedBy = doc.UploadedBy,
                Remark = $"由扫描件 {doc.FileName} 自动生成，待人工确认",
                CreatedAt = DateTimeOffset.UtcNow,
            };

            var lineCount = rnd.Next(1, 4);
            var picked = products.OrderBy(_ => rnd.Next()).Take(lineCount).ToList();
            var no = 1;
            foreach (var p in picked)
            {
                var qty = Math.Max(1, rnd.Next(1, 40));
                order.Lines.Add(new OrderLine
                {
                    OrderId = order.Id,
                    LineNo = no++,
                    ProductId = p.Id,
                    ProductName = p.Name,
                    Qty = qty,
                    UnitPrice = p.UnitPrice,
                    Amount = p.UnitPrice * qty
                });
            }
            order.TotalAmount = order.Lines.Sum(l => l.Amount);

            _db.Orders.Add(order);

            doc.Status = IntakeStatus.Extracted;
            doc.ExtractedJson = JsonSerializer.Serialize(fields);
            doc.Confidence = fields.Min(f => f.Confidence);
            doc.DraftOrderId = order.Id;
            doc.Error = null;
            await _db.SaveChangesAsync(ct);

            await _notifier.SendAsync(doc.UploadedBy,
                $"扫描件已提取 · {doc.FileName}",
                $"已生成草稿订单 {order.OrderNo}（合计 ¥{order.TotalAmount:N2}），最低置信度 {doc.Confidence:P0}，请人工核对。",
                $"/#/orders/{order.Id}", ct);

            _log.LogInformation("OCR 抽取完成 {IntakeId} → {OrderNo}", intakeId, order.OrderNo);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OCR 抽取失败 {IntakeId}", intakeId);
            doc.Status = IntakeStatus.Failed;
            doc.Error = ex.Message;
            await _db.SaveChangesAsync(ct);
        }
    }

    public static List<IntakeExtractedField> Parse(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new List<IntakeExtractedField>()
            : JsonSerializer.Deserialize<List<IntakeExtractedField>>(json) ?? new List<IntakeExtractedField>();
}
