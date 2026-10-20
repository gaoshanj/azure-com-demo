using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Com.Shared.Abstractions;
using Com.Shared.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Com.Shared.Services;

// ─────────────────────────────────────────────────────────────
// Blob：云端走 Azure Blob + 托管标识；本地走文件系统。
// 两条路径都实现同一套语义，业务代码无感。
// ─────────────────────────────────────────────────────────────

public class AzureBlobStore : IBlobStore
{
    private readonly BlobServiceClient _service;
    private readonly string _accountName;
    private readonly string? _immutableDays;
    private readonly ILogger<AzureBlobStore> _log;

    public AzureBlobStore(IConfiguration cfg, ILogger<AzureBlobStore> log)
    {
        _log = log;
        _immutableDays = cfg["WORM_RETENTION_DAYS"];
        var conn = cfg["STORAGE_CONNECTION_STRING"];
        var uri = cfg["STORAGE_BLOB_ENDPOINT"];
        _service = !string.IsNullOrWhiteSpace(conn)
            ? new BlobServiceClient(conn)
            : new BlobServiceClient(new Uri(uri!), new DefaultAzureCredential());
        _accountName = _service.AccountName;
    }

    public string Mode => "azure";

    public async Task EnsureContainersAsync(CancellationToken ct = default)
    {
        foreach (var name in new[] { "contracts", "intake" })
        {
            try
            {
                var c = _service.GetBlobContainerClient(name);
                await c.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                // 容器通常由 Bicep 预先创建并附加 WORM 策略，这里失败不阻断启动
                _log.LogWarning(ex, "容器 {Name} 检查失败（可能已由 IaC 创建）", name);
            }
        }
    }

    public async Task<BlobRef> PutAsync(string container, string path, Stream content, string contentType, CancellationToken ct = default)
    {
        // 关键：合同容器启用了不可变（WORM）策略，blob 一旦写入就不能再改，
        // 所以「先上传、再 SetMetadata 补 sha256」的做法会被策略直接拒绝（409）。
        // 必须先把内容读进内存算出摘要，再在一次 UploadAsync 里连元数据一起写进去。
        var buffer = content as MemoryStream;
        if (buffer is null || !content.CanSeek)
        {
            buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
        }
        buffer.Position = 0;
        var sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(buffer, ct)).ToLowerInvariant();
        buffer.Position = 0;

        var client = _service.GetBlobContainerClient(container).GetBlobClient(path);
        await client.UploadAsync(buffer, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = new Dictionary<string, string> { ["sha256"] = sha }
        }, ct);

        var props = await client.GetPropertiesAsync(cancellationToken: ct);
        var days = int.TryParse(_immutableDays, out var d) && d > 0 ? d : 30;
        var immutable = DateTimeOffset.UtcNow.AddDays(days);

        return new BlobRef(container, path, props.Value.ContentLength, sha, immutable);
    }

    public async Task<BlobContent?> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var client = _service.GetBlobContainerClient(container).GetBlobClient(path);
        if (!(await client.ExistsAsync(ct)).Value) return null;
        var dl = await client.DownloadStreamingAsync(cancellationToken: ct);
        return new BlobContent(dl.Value.Content, dl.Value.Details.ContentType ?? "application/octet-stream", dl.Value.Details.ContentLength);
    }

    public async Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default)
        => (await _service.GetBlobContainerClient(container).GetBlobClient(path).ExistsAsync(ct)).Value;

    public async Task<string?> GetSasUrlAsync(string container, string path, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var udk = await _service.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.Add(ttl), ct);
            var builder = new BlobSasBuilder
            {
                BlobContainerName = container,
                BlobName = path,
                Resource = "b",
                StartsOn = now.AddMinutes(-2),
                ExpiresOn = now.Add(ttl)
            };
            builder.SetPermissions(BlobSasPermissions.Read);
            var blob = _service.GetBlobContainerClient(container).GetBlobClient(path);
            var uri = new BlobUriBuilder(blob.Uri) { Sas = builder.ToSasQueryParameters(udk.Value, _accountName) };
            return uri.ToUri().ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "生成 SAS 失败，回退为 API 流式下载");
            return null;
        }
    }

    public async Task<(long Size, DateTimeOffset? LastModified, DateTimeOffset? ImmutableUntil)?> StatAsync(string container, string path, CancellationToken ct = default)
    {
        try
        {
            var client = _service.GetBlobContainerClient(container).GetBlobClient(path);
            if (!(await client.ExistsAsync(ct)).Value) return null;
            var p = await client.GetPropertiesAsync(cancellationToken: ct);
            return (p.Value.ContentLength, p.Value.LastModified, null);
        }
        catch { return null; }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        // 注意：不要用 BlobServiceClient.GetPropertiesAsync 做探针。
        // 那是管理面调用（GET /?restype=service&comp=properties），只授予了
        // "Storage Blob Data Contributor" 的托管标识会被 403 拒绝，
        // 于是「存储明明是通的、探针却报红」。改用读容器属性即可（纯数据面）。
        try
        {
            await _service.GetBlobContainerClient("contracts").GetPropertiesAsync(cancellationToken: ct);
            return true;
        }
        catch
        {
            try { return (await _service.GetBlobContainerClient("contracts").ExistsAsync(ct)).Value; }
            catch { return false; }
        }
    }
}

public class LocalBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly ILogger<LocalBlobStore> _log;

    public LocalBlobStore(IConfiguration cfg, ILogger<LocalBlobStore> log)
    {
        _log = log;
        _root = cfg["LOCAL_STORAGE_ROOT"] ?? Path.Combine(AppContext.BaseDirectory, "_storage");
        Directory.CreateDirectory(_root);
    }

    public string Mode => "local(file)";

    public Task EnsureContainersAsync(CancellationToken ct = default)
    {
        foreach (var c in new[] { "contracts", "intake" }) Directory.CreateDirectory(Path.Combine(_root, c));
        _log.LogInformation("本地存储根目录 {Root}", _root);
        return Task.CompletedTask;
    }

    private string Full(string container, string path) => Path.Combine(_root, container, path.Replace('/', Path.DirectorySeparatorChar));

    public async Task<BlobRef> PutAsync(string container, string path, Stream content, string contentType, CancellationToken ct = default)
    {
        var full = Full(container, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full)) await content.CopyToAsync(fs, ct);
        var bytes = await File.ReadAllBytesAsync(full, ct);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new BlobRef(container, path, bytes.Length, sha, DateTimeOffset.UtcNow.AddDays(30));
    }

    public Task<BlobContent?> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var full = Full(container, path);
        if (!File.Exists(full)) return Task.FromResult<BlobContent?>(null);
        var fi = new FileInfo(full);
        var ct2 = path.EndsWith(".pdf") ? "application/pdf" : "application/octet-stream";
        return Task.FromResult<BlobContent?>(new BlobContent(File.OpenRead(full), ct2, fi.Length));
    }

    public Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Full(container, path)));

    public Task<string?> GetSasUrlAsync(string container, string path, TimeSpan ttl, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<(long Size, DateTimeOffset? LastModified, DateTimeOffset? ImmutableUntil)?> StatAsync(string container, string path, CancellationToken ct = default)
    {
        var full = Full(container, path);
        if (!File.Exists(full)) return Task.FromResult<(long, DateTimeOffset?, DateTimeOffset?)?>(null);
        var fi = new FileInfo(full);
        return Task.FromResult<(long, DateTimeOffset?, DateTimeOffset?)?>((fi.Length, fi.LastWriteTimeUtc, fi.LastWriteTimeUtc.AddDays(30)));
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(Directory.Exists(_root));
}

// ─────────────────────────────────────────────────────────────
// 消息队列：Service Bus / 进程内
// ─────────────────────────────────────────────────────────────

public class ServiceBusJobQueue : IJobQueue, IAsyncDisposable
{
    public const string QueueContract = "contract-render";
    public const string QueueIntake = "intake-ocr";

    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusJobQueue> _log;

    public ServiceBusJobQueue(IConfiguration cfg, ILogger<ServiceBusJobQueue> log)
    {
        _log = log;
        var conn = cfg["SERVICEBUS_CONNECTION_STRING"];
        var ns = cfg["SERVICEBUS_FULLY_QUALIFIED_NAMESPACE"];
        _client = !string.IsNullOrWhiteSpace(conn)
            ? new ServiceBusClient(conn)
            : new ServiceBusClient(ns!, new DefaultAzureCredential());
    }

    public string Mode => "servicebus";

    public async Task EnqueueAsync(JobMessage message, CancellationToken ct = default)
    {
        var queue = message.Type == JobTypes.ContractRender ? QueueContract : QueueIntake;
        await using var sender = _client.CreateSender(queue);
        var body = BinaryData.FromObjectAsJson(message);
        var msg = new ServiceBusMessage(body)
        {
            ContentType = "application/json",
            MessageId = message.EntityId.ToString(),
            Subject = message.Type
        };
        await sender.SendMessageAsync(msg, ct);
        _log.LogInformation("已投递 {Type} → {Queue} ({EntityId})", message.Type, queue, message.EntityId);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var receiver = _client.CreateReceiver(QueueContract);
            await receiver.PeekMessagesAsync(1, cancellationToken: ct);
            return true;
        }
        catch { return false; }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>本地/降级模式：直接在工作线程执行处理器，等价于"同步异步化"。</summary>
public class LocalJobQueue : IJobQueue
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LocalJobQueue> _log;

    public LocalJobQueue(IServiceScopeFactory scopes, ILogger<LocalJobQueue> log) { _scopes = scopes; _log = log; }

    public string Mode => "in-process";

    public Task EnqueueAsync(JobMessage message, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                switch (message.Type)
                {
                    case JobTypes.ContractRender:
                        await scope.ServiceProvider.GetRequiredService<Com.Shared.Workers.ContractWorkHandler>()
                            .HandleAsync(message.EntityId, CancellationToken.None);
                        break;
                    case JobTypes.IntakeOcr:
                        await scope.ServiceProvider.GetRequiredService<Com.Shared.Workers.IntakeWorkHandler>()
                            .HandleAsync(message.EntityId, CancellationToken.None);
                        break;
                }
            }
            catch (Exception ex) { _log.LogError(ex, "本地任务执行失败 {Type}", message.Type); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
}

// ─────────────────────────────────────────────────────────────
// 缓存 / 通知 / 审计镜像 / 工作流桥
// ─────────────────────────────────────────────────────────────

public class MemoryCacheStore : ICacheStore
{
    private readonly IMemoryCache _cache;
    public MemoryCacheStore(IMemoryCache cache) => _cache = cache;
    public string Mode => "memory";
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => Task.FromResult(_cache.TryGetValue(key, out T? v) ? v : default);
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) { _cache.Set(key, value, ttl); return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct = default) { _cache.Remove(key); return Task.CompletedTask; }
    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
}

public class NotificationService : INotifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(IServiceScopeFactory scopes, ILogger<NotificationService> log) { _scopes = scopes; _log = log; }

    public string Mode => "station";

    public async Task SendAsync(string upn, string title, string body, string? link, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upn)) return;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Com.Shared.Data.ComDbContext>();
        db.Notifications.Add(new Com.Shared.Domain.Notification
        {
            UserUpn = upn, Title = title, Body = body, Link = link, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        _log.LogInformation("通知 → {Upn} | {Title}", upn, title);
    }
}

public class CosmosAuditMirror : IAuditMirror
{
    private readonly Microsoft.Azure.Cosmos.Container? _container;
    private readonly ILogger<CosmosAuditMirror> _log;

    public CosmosAuditMirror(IConfiguration cfg, ILogger<CosmosAuditMirror> log)
    {
        _log = log;
        var ep = cfg["COSMOS_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(ep)) return;
        try
        {
            var client = new Microsoft.Azure.Cosmos.CosmosClient(ep, new DefaultAzureCredential());
            _container = client.GetContainer(cfg["COSMOS_DATABASE"] ?? "com", cfg["COSMOS_CONTAINER"] ?? "audit");
        }
        catch (Exception ex) { _log.LogWarning(ex, "Cosmos 客户端初始化失败，审计镜像关闭"); }
    }

    public string Mode => _container == null ? "off" : "cosmos";

    public async Task WriteAsync(Com.Shared.Domain.AuditEvent evt, CancellationToken ct = default)
    {
        if (_container == null) return;
        try
        {
            await _container.CreateItemAsync(new
            {
                id = evt.Id.ToString(),
                entityType = evt.EntityType,
                entityId = evt.EntityId,
                action = evt.Action,
                fromStatus = evt.FromStatus,
                toStatus = evt.ToStatus,
                actor = evt.Actor,
                traceId = evt.TraceId,
                payload = evt.Payload,
                createdAt = evt.CreatedAt,
                ttl = 60 * 60 * 24 * 30
            }, new Microsoft.Azure.Cosmos.PartitionKey(evt.EntityId), cancellationToken: ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "审计镜像写入失败（不影响主流程）"); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_container == null) return false;
        try { await _container.ReadItemAsync<object>("__ping__", new Microsoft.Azure.Cosmos.PartitionKey("__ping__"), cancellationToken: ct); return true; }
        catch (Microsoft.Azure.Cosmos.CosmosException ce) when (ce.StatusCode == System.Net.HttpStatusCode.NotFound) { return true; }
        catch { return false; }
    }
}

/// <summary>调用 Logic Apps 的 HTTP 触发器，启动一级审批编排。</summary>
public class LogicAppsBridge : IWorkflowBridge
{
    private readonly HttpClient _http;
    private readonly string? _triggerUrl;
    private readonly string? _key;
    private readonly ILogger<LogicAppsBridge> _log;

    public LogicAppsBridge(HttpClient http, IConfiguration cfg, ILogger<LogicAppsBridge> log)
    {
        _http = http; _log = log;
        _triggerUrl = cfg["LOGICAPP_TRIGGER_URL"];
        _key = cfg["INTERNAL_KEY"];
    }

    public string Mode => string.IsNullOrWhiteSpace(_triggerUrl) ? "internal" : "logicapps";

    public async Task<string?> StartAsync(Guid taskId, Guid orderId, int level, string approverUpn, decimal amount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_triggerUrl)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _triggerUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    taskId, orderId, level, approverUpn, amount
                }), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            resp.Headers.TryGetValues("x-ms-workflow-run-id", out var runIds);
            var runId = runIds?.FirstOrDefault();
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("启动 Logic Apps 失败 {Code}", resp.StatusCode);
            return runId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "启动 Logic Apps 异常，将回退为内部推进");
            return null;
        }
    }
}

public class InternalWorkflowBridge : IWorkflowBridge
{
    public string Mode => "internal";
    public Task<string?> StartAsync(Guid taskId, Guid orderId, int level, string approverUpn, decimal amount, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
