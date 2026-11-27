using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Thin Hangfire adapter that implements <see cref="IHangfireFlowTrigger"/> and
/// <see cref="IHangfireStepRunner"/> by delegating to <see cref="IFlowOrchestrator"/>.
/// Its only job is to rebuild the engine arguments from the job payload, record the Hangfire job ID
/// on the execution context, and call the runtime-neutral engine.
/// </summary>
/// <remarks>
/// Step jobs carry a <see cref="HangfireStepEnvelope"/>: the flow is resolved from
/// <see cref="FlowRegistry"/> by id, and the engine reloads the trigger payload and headers from the
/// run's stored trigger data, so neither the manifest nor user payloads go through Hangfire's
/// Newtonsoft.Json argument store.
/// </remarks>
public sealed class HangfireFlowOrchestrator : IHangfireFlowTrigger, IHangfireStepRunner
{
    private readonly IFlowOrchestrator _engine;
    private readonly FlowRegistry _flows;
    private readonly FlowOrchestratorTelemetry? _telemetry;

    /// <summary>Initialises the adapter with the core execution engine, the flow registry used to resolve definitions on the worker, and an optional telemetry hub for cron-lag metrics.</summary>
    /// <param name="engine">The runtime-neutral engine every job delegates to.</param>
    /// <param name="flows">Registry used to resolve the flow named by a step job.</param>
    /// <param name="telemetry">Optional — when supplied, cron-lag is recorded for schedule-triggered jobs.</param>
    public HangfireFlowOrchestrator(
        IFlowOrchestrator engine,
        FlowRegistry flows,
        FlowOrchestratorTelemetry? telemetry = null)
    {
        _engine = engine;
        _flows = flows;
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
    public ValueTask<object?> RunStepAsync(HangfireStepEnvelope envelope, PerformContext? performContext = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var flow = ResolveFlow(envelope.FlowId, envelope.StepKey);
        var ctx = envelope.ToExecutionContext(performContext?.BackgroundJob?.Id);
        return _engine.RunStepAsync(ctx, flow, envelope.ToStepInstance());
    }

    /// <inheritdoc/>
    [Obsolete("Only resolves step jobs enqueued before 2.0. New jobs use RunStepAsync(HangfireStepEnvelope, PerformContext?). Drain those queues before the next major release removes this overload.")]
    public ValueTask<object?> RunStepAsync(IExecutionContext ctx, Guid flowId, IStepInstance step, PerformContext? performContext = null)
    {
        ctx.JobId = performContext?.BackgroundJob?.Id;
        var flow = ResolveFlow(flowId, step.Key);
        // 1.x stored JsonElement inputs / trigger data through Newtonsoft, which destroys them; a
        // re-dispatched Pending step (poll re-check, WaitForSignal deadline check) would throw on its
        // first read. Restore them from the manifest before the engine sees the step.
        LegacyStepJobRepair.Repair(ctx, step, flow);
        return _engine.RunStepAsync(ctx, flow, step);
    }

    /// <inheritdoc/>
    public ValueTask<object?> RetryStepAsync(Guid flowId, Guid runId, string stepKey, PerformContext? performContext = null)
        => _engine.RetryStepAsync(flowId, runId, stepKey);

    private IFlowDefinition ResolveFlow(Guid flowId, string stepKey)
        => _flows.Find(flowId)
            ?? throw new InvalidOperationException($"Flow {flowId} is not registered. Cannot dispatch step '{stepKey}'.");
}
