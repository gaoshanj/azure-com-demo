using Com.Shared.Domain;

namespace Com.Shared.Abstractions;

// ─────────────────────────────────────────────────────────────
// 每个 Azure 依赖一个接口 + 两个实现（云端 / 本地）。
// 业务代码永远只依赖接口，切换靠配置，不靠 if。
// ─────────────────────────────────────────────────────────────

public record BlobRef(string Container, string Path, long Size, string Sha256, DateTimeOffset? ImmutableUntil);

public record BlobContent(Stream Stream, string ContentType, long Size) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public interface IBlobStore
{
    string Mode { get; }
    Task EnsureContainersAsync(CancellationToken ct = default);
    Task<BlobRef> PutAsync(string container, string path, Stream content, string contentType, CancellationToken ct = default);
    Task<BlobContent?> OpenReadAsync(string container, string path, CancellationToken ct = default);
    Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default);
    /// <summary>返回短时只读 SAS；本地实现返回 null（改由 API 流式下发）。</summary>
    Task<string?> GetSasUrlAsync(string container, string path, TimeSpan ttl, CancellationToken ct = default);
    Task<(long Size, DateTimeOffset? LastModified, DateTimeOffset? ImmutableUntil)?> StatAsync(string container, string path, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public record JobMessage(string Type, Guid EntityId, Dictionary<string, string>? Data = null);

public static class JobTypes
{
    public const string ContractRender = "contract-render";
    public const string IntakeOcr = "intake-ocr";

    public static string QueueName(string type) => type switch
    {
        ContractRender => "contract-render",
        IntakeOcr => "intake-ocr",
        _ => type
    };
}

public interface IJobQueue
{
    string Mode { get; }
    Task EnqueueAsync(JobMessage message, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public interface ICacheStore
{
    string Mode { get; }
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public interface INotifier
{
    string Mode { get; }
    Task SendAsync(string upn, string title, string body, string? link, CancellationToken ct = default);
}

public interface IAuditMirror
{
    string Mode { get; }
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
    Task<bool> PingAsync(CancellationToken ct = default);
}

public record ContractRenderModel(
    string OrderNo,
    string CustomerName,
    string CustomerCode,
    string TaxId,
    decimal TotalAmount,
    string Currency,
    string Terms,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? Remark,
    IReadOnlyList<ContractLine> Lines,
    IReadOnlyList<string> ApprovalTrail);

public record ContractLine(int LineNo, string ProductName, int Qty, decimal UnitPrice, decimal Amount);

public interface IPdfRenderer
{
    byte[] Render(ContractRenderModel model);
    bool Ping();
}

/// <summary>工作流回调（Logic Apps HttpWebhook）在 API 侧的落点。</summary>
public interface IWorkflowBridge
{
    string Mode { get; }
    /// <summary>提交后启动一级审批编排，返回运行标识（可为空）。</summary>
    Task<string?> StartAsync(Guid taskId, Guid orderId, int level, string approverUpn, decimal amount, CancellationToken ct = default);
}
