namespace Com.Shared.Domain;

// ─────────────────────────────────────────────────────────────
// 枚举：状态机与审批的唯一取值来源
// ─────────────────────────────────────────────────────────────

public enum OrderStatus
{
    Draft,            // 草稿
    PendingApproval,  // 审批中
    Approved,         // 审批通过
    Rejected,         // 驳回
    Contracted,       // 合同生成中/已生成
    Archived,         // 已归档（WORM）
    Cancelled         // 已取消
}

public enum ApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3
}

public enum ContractStatus
{
    Generating = 0,
    Ready = 1,
    Archived = 2,
    Failed = 3
}

public enum IntakeStatus
{
    Queued = 0,
    Extracting = 1,
    Extracted = 2,
    Failed = 3
}

// ─────────────────────────────────────────────────────────────
// 实体
// ─────────────────────────────────────────────────────────────

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Upn { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";          // sales / approver1 / finance / admin
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string TaxId { get; set; } = "";
    public decimal CreditLimit { get; set; }
    public string Status { get; set; } = "Active";
}

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = "CNY";
    public bool Active { get; set; } = true;
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNo { get; set; } = "";
    public Guid CustomerId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "CNY";
    public string Terms { get; set; } = "款到发货";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Remark { get; set; }

    // 乐观并发令牌：由应用层自增，不用数据库 rowversion（跨 SQLite / SQL Server 一致）
    public long Version { get; set; }

    public List<OrderLine> Lines { get; set; } = new();
}

public class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public int Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

public class ApprovalTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public int Level { get; set; }
    public string ApproverRole { get; set; } = "";
    public string ApproverUpn { get; set; } = "";
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueAt { get; set; }
    public string? WorkflowRunId { get; set; }

    /// <summary>Logic Apps 回调地址。等价于凭据，绝不出现在任何对外 DTO 中。</summary>
    public string? CallbackUrl { get; set; }
}

public class Contract
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public int Version { get; set; } = 1;
    public string BlobContainer { get; set; } = "contracts";
    public string? BlobPath { get; set; }
    public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset? ImmutableUntil { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Generating;
    public string? Error { get; set; }
    public string GeneratedBy { get; set; } = "worker";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = "Order";
    public string EntityId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string Actor { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserUpn { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public bool Read { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class IntakeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string BlobContainer { get; set; } = "intake";
    public string BlobPath { get; set; } = "";
    public IntakeStatus Status { get; set; } = IntakeStatus.Queued;
    public string? ExtractedJson { get; set; }
    public double Confidence { get; set; }
    public Guid? DraftOrderId { get; set; }
    public string? Error { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ─────────────────────────────────────────────────────────────
// 状态机：所有状态变更的唯一入口
// ─────────────────────────────────────────────────────────────

public class InvalidTransitionException : Exception
{
    public InvalidTransitionException(OrderStatus from, string action)
        : base($"订单状态 {from} 不允许执行 {action}") { }
}

public static class OrderStateMachine
{
    private static readonly Dictionary<(OrderStatus, string), OrderStatus> Map = new()
    {
        [(OrderStatus.Draft, "submit")] = OrderStatus.PendingApproval,
        [(OrderStatus.Draft, "cancel")] = OrderStatus.Cancelled,
        [(OrderStatus.PendingApproval, "approve")] = OrderStatus.PendingApproval, // 可能还有下一级
        [(OrderStatus.PendingApproval, "reject")] = OrderStatus.Rejected,
        [(OrderStatus.PendingApproval, "cancel")] = OrderStatus.Cancelled,
        [(OrderStatus.Approved, "generate")] = OrderStatus.Contracted,
        [(OrderStatus.Contracted, "archive")] = OrderStatus.Archived,
        [(OrderStatus.Rejected, "revise")] = OrderStatus.Draft,
    };

    public static bool Can(OrderStatus from, string action) => Map.ContainsKey((from, action));

    public static OrderStatus Next(OrderStatus from, string action)
        => Map.TryGetValue((from, action), out var to) ? to : throw new InvalidTransitionException(from, action);

    public static string Label(OrderStatus s) => s switch
    {
        OrderStatus.Draft => "草稿",
        OrderStatus.PendingApproval => "审批中",
        OrderStatus.Approved => "已通过",
        OrderStatus.Rejected => "已驳回",
        OrderStatus.Contracted => "已生成合同",
        OrderStatus.Archived => "已归档",
        OrderStatus.Cancelled => "已取消",
        _ => s.ToString()
    };
}

// ─────────────────────────────────────────────────────────────
// 审批分档规则：改这里就能改业务，不需要动端点
// ─────────────────────────────────────────────────────────────

public static class ApprovalRules
{
    public const decimal Tier1 = 500_000m;
    public const decimal Tier2 = 2_000_000m;

    /// <summary>按金额返回依次需要的审批角色（串行）。</summary>
    public static IReadOnlyList<string> Chain(decimal total) => total switch
    {
        < Tier1 => new[] { Roles.Approver1 },
        < Tier2 => new[] { Roles.Approver1, Roles.Finance },
        _       => new[] { Roles.Approver1, Roles.Finance, Roles.Admin }
    };

    public static int MaxLevel(decimal total) => Chain(total).Count;

    public static string Describe(decimal total) => total switch
    {
        < Tier1 => $"1 级审批（¥{Tier1:N0} 以下）",
        < Tier2 => $"2 级审批（¥{Tier1:N0} – ¥{Tier2:N0}）",
        _       => $"3 级审批（¥{Tier2:N0} 以上）"
    };
}

public static class Roles
{
    public const string Sales = "sales";
    public const string Approver1 = "approver1";
    public const string Finance = "finance";
    public const string Admin = "admin";

    public static string Label(string role) => role switch
    {
        Sales => "销售",
        Approver1 => "销售总监",
        Finance => "财务",
        Admin => "总经理",
        _ => role
    };
}
