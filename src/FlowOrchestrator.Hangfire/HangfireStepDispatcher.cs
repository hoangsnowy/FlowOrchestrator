using System.Diagnostics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using Hangfire;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hangfire implementation of <see cref="IStepDispatcher"/> that submits steps
/// as Hangfire background jobs targeting <see cref="IHangfireStepRunner"/>.
/// </summary>
internal sealed class HangfireStepDispatcher : IStepDispatcher
{
    private const string RuntimeName = "hangfire";

    private readonly IBackgroundJobClient _client;
    private readonly FlowOrchestratorTelemetry? _telemetry;

    /// <summary>Initialises the dispatcher with the Hangfire job client.</summary>
    /// <param name="client">Hangfire job client the steps are submitted through.</param>
    /// <param name="telemetry">Optional — when supplied, each submission is timed on <c>flow_step_dispatch_duration_ms</c>.</param>
    /// <param name="observability">Optional — <see cref="FlowObservabilityOptions.EnableOpenTelemetry"/> gates the timing.</param>
    public HangfireStepDispatcher(
        IBackgroundJobClient client,
        FlowOrchestratorTelemetry? telemetry = null,
        FlowObservabilityOptions? observability = null)
    {
        _client = client;
        _telemetry = observability?.EnableOpenTelemetry == false ? null : telemetry;
    }

    /// <inheritdoc/>
    public ValueTask<string?> EnqueueStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        CancellationToken ct)
    {
        // Pass only flow.Id through Hangfire's argument store. The runner rehydrates the
        // full IFlowDefinition via IFlowRepository — this avoids serialising the manifest
        // (and types like RunAfterCondition) through Hangfire's Newtonsoft.Json serializer.
        var flowId = flow.Id;
        var start = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            var id = _client.Enqueue<IHangfireStepRunner>(
                r => r.RunStepAsync(context, flowId, step, null));
            ok = true;
            return new(id);
        }
        finally
        {
            _telemetry?.RecordDispatch(RuntimeName, scheduled: false, ok, start);
        }
    }

    /// <inheritdoc/>
    public ValueTask<string?> ScheduleStepAsync(
        IExecutionContext context,
        IFlowDefinition flow,
        IStepInstance step,
        TimeSpan delay,
        CancellationToken ct)
    {
        var flowId = flow.Id;
        var start = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            var id = _client.Schedule<IHangfireStepRunner>(
                r => r.RunStepAsync(context, flowId, step, null),
                delay);
            ok = true;
            return new(id);
        }
        finally
        {
            _telemetry?.RecordDispatch(RuntimeName, scheduled: true, ok, start);
        }
    }
}
