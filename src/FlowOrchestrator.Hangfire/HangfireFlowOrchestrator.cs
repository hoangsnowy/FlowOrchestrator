using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Thin Hangfire adapter that implements <see cref="IHangfireFlowTrigger"/> and
/// <see cref="IHangfireStepRunner"/> by delegating to <see cref="IFlowOrchestrator"/>.
/// Its only job is to extract the Hangfire job ID from <see cref="PerformContext"/> and
/// inject it into the execution context before calling the runtime-neutral engine.
/// </summary>
/// <remarks>
/// Step jobs receive only a <see cref="Guid"/> flow identifier, not the full
/// <see cref="IFlowDefinition"/>. The flow is rehydrated via <see cref="IFlowRepository"/>
/// before delegating to the engine. This avoids serialising the manifest through Hangfire's
/// Newtonsoft.Json-based argument store, which would auto-detect collection-typed
/// abstractions like <c>RunAfterCondition</c> as JSON arrays.
/// </remarks>
public sealed class HangfireFlowOrchestrator : IHangfireFlowTrigger, IHangfireStepRunner
{
    private readonly IFlowOrchestrator _engine;
    private readonly IFlowRepository _flowRepository;
    private readonly FlowOrchestratorTelemetry? _telemetry;

    /// <summary>Initialises the adapter with the core execution engine, the flow repository used to rehydrate definitions on the worker, and an optional telemetry hub for cron-lag metrics.</summary>
    public HangfireFlowOrchestrator(
        IFlowOrchestrator engine,
        IFlowRepository flowRepository,
        FlowOrchestratorTelemetry? telemetry = null)
    {
        _engine = engine;
        _flowRepository = flowRepository;
        _telemetry = telemetry;
    }

    /// <inheritdoc/>
    public ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null)
    {
        triggerContext.JobId = performContext?.BackgroundJob?.Id;
        return _engine.TriggerAsync(triggerContext);
    }

    /// <inheritdoc/>
    public ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, PerformContext? performContext = null)
    {
        // Cron lag = wall-clock between Hangfire enqueueing the job (when the recurring scheduler
        // fired internally) and the worker picking it up. Hangfire does not expose the cron's
        // exact scheduled tick time, but BackgroundJob.CreatedAt is the closest proxy and is
        // what operators care about: "how far behind is the cron pipeline?".
        if (_telemetry is not null && performContext?.BackgroundJob?.CreatedAt is { } createdAt)
        {
            var lagMs = Math.Max(0, (DateTime.UtcNow - createdAt).TotalMilliseconds);
            _telemetry.CronLagMs.Record(
                lagMs,
                new KeyValuePair<string, object?>("flow_id", flowId.ToString()),
                new KeyValuePair<string, object?>("trigger_key", triggerKey),
                new KeyValuePair<string, object?>("runtime", "hangfire"));
        }

        return _engine.TriggerByScheduleAsync(flowId, triggerKey, performContext?.BackgroundJob?.Id);
    }

    /// <inheritdoc/>
    public async ValueTask<object?> RunStepAsync(IExecutionContext ctx, Guid flowId, IStepInstance step, PerformContext? performContext = null)
    {
        ctx.JobId = performContext?.BackgroundJob?.Id;
        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} is not registered. Cannot dispatch step '{step.Key}'.");
        return await _engine.RunStepAsync(ctx, flow, step).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [Obsolete("Use the Guid flowId overload. This method exists only so Hangfire can resolve job payloads enqueued before Plan 05.")]
    public ValueTask<object?> RunStepAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step, PerformContext? performContext = null)
        => RunStepAsync(ctx, flow.Id, step, performContext);

    /// <inheritdoc/>
    public ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null)
        => _engine.RetryStepAsync(flowId, runId, stepKey);
}
