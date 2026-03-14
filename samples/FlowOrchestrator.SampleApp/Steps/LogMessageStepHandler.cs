using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

public sealed class LogMessageStepHandler : IStepHandler
{
    private readonly ILogger<LogMessageStepHandler> _logger;

    public LogMessageStepHandler(ILogger<LogMessageStepHandler> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var msg = step.Inputs.TryGetValue("message", out var m) ? m?.ToString() : step.Key;
        _logger.LogInformation("[FlowOrchestrator] RunId={RunId} Step={StepKey} => {Message}", ctx.RunId, step.Key, msg);
        return ValueTask.FromResult<object?>(new { logged = msg });
    }
}
