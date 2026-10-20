using System.Text.Json;
using Com.Shared.Abstractions;
using Com.Shared.Workers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Com.Worker;

/// <summary>
/// 无服务器工作流：消息一到就干活，干完就释放。
/// 与 API 里的内联模式调用的是同一个 Handler —— 换宿主不换逻辑。
/// </summary>
public class ContractRenderFunction
{
    private readonly ContractWorkHandler _handler;
    private readonly ILogger<ContractRenderFunction> _log;

    public ContractRenderFunction(ContractWorkHandler handler, ILogger<ContractRenderFunction> log)
    {
        _handler = handler; _log = log;
    }

    [Function("contract-render")]
    public async Task Run(
        [ServiceBusTrigger("contract-render", Connection = "ServiceBusConnection")] string message,
        CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<JobMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (job == null) { _log.LogWarning("无法解析任务消息"); return; }

        _log.LogInformation("收到合同渲染任务 {ContractId}", job.EntityId);
        await _handler.HandleAsync(job.EntityId, ct);
    }
}

public class IntakeOcrFunction
{
    private readonly IntakeWorkHandler _handler;
    private readonly ILogger<IntakeOcrFunction> _log;

    public IntakeOcrFunction(IntakeWorkHandler handler, ILogger<IntakeOcrFunction> log)
    {
        _handler = handler; _log = log;
    }

    [Function("intake-ocr")]
    public async Task Run(
        [ServiceBusTrigger("intake-ocr", Connection = "ServiceBusConnection")] string message,
        CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<JobMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (job == null) { _log.LogWarning("无法解析任务消息"); return; }

        _log.LogInformation("收到扫描件抽取任务 {IntakeId}", job.EntityId);
        await _handler.HandleAsync(job.EntityId, ct);
    }
}
