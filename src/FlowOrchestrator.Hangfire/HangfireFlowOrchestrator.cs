using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Thin Hangfire adapter that implements <see cref="IHangfireFlowTrigger"/> and
/// <see cref="IHangfireStepRunner"/> by delegating to <see cref="IFlowOrchestrator"/>.
/// Its only job is to extract the Hangfire job ID from <see cref="PerformContext"/> and
/// inject it into the execution context before calling the runtime-neutral engine.
/// </summary>
public sealed class HangfireFlowOrchestrator : IHangfireFlowTrigger, IHangfireStepRunner
{
    private readonly IFlowOrchestrator _engine;

    /// <summary>Initialises the adapter with the core execution engine.</summary>
    public HangfireFlowOrchestrator(IFlowOrchestrator engine) => _engine = engine;

    /// <inheritdoc/>
    public ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null)
    {
        triggerContext.JobId = performContext?.BackgroundJob?.Id;
        return _engine.TriggerAsync(triggerContext);
    }

    /// <inheritdoc/>
    public ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, PerformContext? performContext = null)
        => _engine.TriggerByScheduleAsync(flowId, triggerKey, performContext?.BackgroundJob?.Id);

    /// <inheritdoc/>
    public ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null)
    {
        ctx.JobId = performContext?.BackgroundJob?.Id;
        return _engine.RunStepAsync(ctx, flow, step);
    }

    /// <inheritdoc/>
    public ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null)
        => _engine.RetryStepAsync(flowId, runId, stepKey);
}
