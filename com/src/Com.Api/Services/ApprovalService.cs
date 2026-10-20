using System.Text;
using System.Text.Json;
using Com.Shared.Abstractions;
using Com.Shared.Data;
using Com.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Com.Api.Services;

/// <summary>
/// 审批编排的全部业务规则集中在这里：分档、串行推进、回调恢复、幂等。
/// 放在服务层而不是端点里，是因为 Logic Apps 回调也要走同一套规则（只有一份真相）。
/// </summary>
public class ApprovalService
{
    private readonly ComDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IWorkflowBridge _wf;
    private readonly INotifier _notifier;
    private readonly IAuditMirror _mirror;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ApprovalService> _log;

    public ApprovalService(ComDbContext db, IJobQueue jobs, IWorkflowBridge wf, INotifier notifier,
                           IAuditMirror mirror, IHttpClientFactory http, ILogger<ApprovalService> log)
    {
        _db = db; _jobs = jobs; _wf = wf; _notifier = notifier; _mirror = mirror; _http = http; _log = log;
    }

    public string WorkflowMode => _wf.Mode;

    private string TraceId => System.Diagnostics.Activity.Current?.Id ?? "local";

    private void Audit(Guid orderId, string action, string? from, string? to, string actor, object? payload = null)
    {
        var evt = new AuditEvent
        {
            EntityType = "Order",
            EntityId = orderId.ToString(),
            Action = action,
            FromStatus = from,
            ToStatus = to,
            Actor = actor,
            TraceId = TraceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = payload == null ? null : JsonSerializer.Serialize(payload)
        };
        _db.AuditEvents.Add(evt);
        _ = _mirror.WriteAsync(evt);
    }

    private async Task<string> UpnForRoleAsync(string role, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Role == role && x.Active, ct);
        return u?.Upn ?? $"{role}@com.demo";
    }

    /// <summary>提交订单：生成一级审批任务并启动编排。</summary>
    public async Task SubmitAsync(Order order, string actor, CancellationToken ct = default)
    {
        if (order.Status != OrderStatus.Draft)
            throw new InvalidTransitionException(order.Status, "submit");
        if (order.Lines.Count == 0)
            throw new ValidationProblemException("订单没有明细，无法提交", new { lines = new[] { "至少需要一行明细" } });

        var credit = await _db.Customers.Where(c => c.Id == order.CustomerId).Select(c => c.CreditLimit).FirstOrDefaultAsync(ct);
        if (credit > 0 && order.TotalAmount > credit)
            throw new ValidationProblemException(
                $"订单金额 ¥{order.TotalAmount:N2} 超出客户信用额度 ¥{credit:N2}",
                new { totalAmount = new[] { "超出客户信用额度" } });

        order.Status = OrderStateMachine.Next(order.Status, "submit");
        order.SubmittedAt = DateTimeOffset.UtcNow;
        order.Version++;
        Audit(order.Id, "order.submitted", "Draft", order.Status.ToString(), actor);

        var task = await CreateTaskAsync(order, 1, ct);
        await _db.SaveChangesAsync(ct);

        var runId = await _wf.StartAsync(task.Id, order.Id, 1, task.ApproverUpn, order.TotalAmount, ct);
        if (runId != null)
        {
            task.WorkflowRunId = runId;
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            // 未启用工作流时，通知由 API 直接发（两条路径的用户体验一致）
            await NotifyApproverAsync(order, task, ct);
        }

        _log.LogInformation("订单 {OrderNo} 已提交，审批链 {Levels} 级，工作流 {Mode}",
            order.OrderNo, ApprovalRules.MaxLevel(order.TotalAmount), _wf.Mode);
    }

    public async Task<ApprovalTask> CreateTaskAsync(Order order, int level, CancellationToken ct = default)
    {
        var chain = ApprovalRules.Chain(order.TotalAmount);
        var role = chain[level - 1];
        var upn = await UpnForRoleAsync(role, ct);

        var existing = await _db.ApprovalTasks.FirstOrDefaultAsync(
            t => t.OrderId == order.Id && t.Level == level, ct);
        if (existing != null) return existing;

        var task = new ApprovalTask
        {
            OrderId = order.Id,
            Level = level,
            ApproverRole = role,
            ApproverUpn = upn,
            Decision = ApprovalDecision.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            DueAt = DateTimeOffset.UtcNow.AddHours(48)
        };
        _db.ApprovalTasks.Add(task);
        return task;
    }

    public async Task NotifyApproverAsync(Order order, ApprovalTask task, CancellationToken ct = default)
    {
        var customer = await _db.Customers.Where(c => c.Id == order.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        await _notifier.SendAsync(task.ApproverUpn,
            $"待审批 · {order.OrderNo}（L{task.Level}）",
            $"客户 {customer} 提交订单 ¥{order.TotalAmount:N2}，{ApprovalRules.Describe(order.TotalAmount)}，请在 48 小时内处理。",
            $"/#/orders/{order.Id}", ct);
    }

    /// <summary>审批人做出决策。</summary>
    public async Task<object> DecideAsync(Guid taskId, string decision, string? comment, string actor, CancellationToken ct = default)
    {
        var task = await _db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
                   ?? throw new KeyNotFoundException("审批任务不存在");
        if (!string.Equals(task.ApproverUpn, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("该审批任务不属于当前用户");
        if (task.Decision != ApprovalDecision.Pending)
            throw new InvalidTransitionException(OrderStatus.PendingApproval, $"审批（已处理：{task.Decision}）");

        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == task.OrderId, ct)
                    ?? throw new KeyNotFoundException("订单不存在");

        var approved = decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        task.Decision = approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        task.DecidedAt = DateTimeOffset.UtcNow;
        task.Comment = comment;
        Audit(order.Id, approved ? $"approval.approved.L{task.Level}" : $"approval.rejected.L{task.Level}",
            null, null, actor, new { comment });
        await _db.SaveChangesAsync(ct);

        if (!approved)
        {
            order.Status = OrderStateMachine.Next(OrderStatus.PendingApproval, "reject");
            order.Version++;
            Audit(order.Id, "order.rejected", "PendingApproval", "Rejected", actor);
            await _db.SaveChangesAsync(ct);
            await _notifier.SendAsync(order.CreatedBy, $"订单被驳回 · {order.OrderNo}",
                $"L{task.Level} {Roles.Label(task.ApproverRole)} 驳回：{comment ?? "无意见"}", $"/#/orders/{order.Id}", ct);
            await TryResumeWorkflowAsync(task, ct);
            return new { status = order.Status.ToString(), message = "已驳回" };
        }

        // 批准：优先让 Logic Apps 恢复并推进（回调），失败则本地推进兜底
        var resumed = await TryResumeWorkflowAsync(task, ct);
        if (!resumed)
            await AdvanceAsync(order.Id, actor, ct);

        return new
        {
            status = "accepted",
            workflow = resumed ? "resumed" : "inline",
            message = resumed ? "已批准，工作流已恢复" : "已批准，已由 API 直接推进"
        };
    }

    /// <summary>POST 到 Logic Apps 的回调 URL，让挂起的工作流恢复。</summary>
    private async Task<bool> TryResumeWorkflowAsync(ApprovalTask task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.CallbackUrl)) return false;
        try
        {
            var client = _http.CreateClient("callback");
            client.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post, task.CallbackUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    taskId = task.Id,
                    decision = task.Decision.ToString(),
                    comment = task.Comment
                }), Encoding.UTF8, "application/json")
            };
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("工作流回调失败 {Code}，回退本地推进", resp.StatusCode);
                return false;
            }
            task.CallbackUrl = null;
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "工作流回调异常，回退本地推进");
            return false;
        }
    }

    /// <summary>推进到下一级，或完成审批。幂等：重复调用不会重复建任务。</summary>
    public async Task AdvanceAsync(Guid orderId, string actor, CancellationToken ct = default)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct)
                    ?? throw new KeyNotFoundException("订单不存在");
        var tasks = await _db.ApprovalTasks.Where(t => t.OrderId == orderId).OrderBy(t => t.Level).ToListAsync(ct);

        if (tasks.Any(t => t.Decision == ApprovalDecision.Rejected))
        {
            if (order.Status != OrderStatus.Rejected)
            {
                order.Status = OrderStatus.Rejected;
                order.Version++;
                Audit(order.Id, "order.rejected", "PendingApproval", "Rejected", actor);
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        var chain = ApprovalRules.Chain(order.TotalAmount);
        var approvedLevels = tasks.Count(t => t.Decision == ApprovalDecision.Approved);

        if (approvedLevels < chain.Count)
        {
            var nextLevel = approvedLevels + 1;
            var next = await CreateTaskAsync(order, nextLevel, ct);
            await _db.SaveChangesAsync(ct);
            var runId = await _wf.StartAsync(next.Id, order.Id, nextLevel, next.ApproverUpn, order.TotalAmount, ct);
            if (runId == null) await NotifyApproverAsync(order, next, ct);
            else { next.WorkflowRunId = runId; await _db.SaveChangesAsync(ct); }
            _log.LogInformation("订单 {OrderNo} 进入 L{Level}", order.OrderNo, nextLevel);
            return;
        }

        if (order.Status != OrderStatus.Approved)
        {
            order.Status = OrderStatus.Approved;
            order.Version++;
            Audit(order.Id, "order.approved", "PendingApproval", "Approved", actor,
                new { levels = chain.Count });
            await _db.SaveChangesAsync(ct);
            await _notifier.SendAsync(order.CreatedBy, $"订单已通过 · {order.OrderNo}",
                $"{chain.Count} 级审批全部通过，可以生成合同。", $"/#/orders/{order.Id}", ct);
        }
    }
}
