using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Application-facing entry point for delivering signals to parked <c>WaitForSignal</c> steps.
/// Used by the dashboard signal endpoint and by tests/applications that prefer not to talk to HTTP.
/// </summary>
public interface IFlowSignalDispatcher
{
    /// <summary>
    /// Validates the run, persists the payload on the matching waiter, and nudges the engine
    /// to re-execute the parked step so it observes the delivered payload.
    /// </summary>
    /// <param name="runId">The run whose <c>WaitForSignal</c> step should receive the signal.</param>
    /// <param name="signalName">Logical signal name configured on the step's <c>signalName</c> input.</param>
    /// <param name="payloadJson">Pre-serialised JSON payload supplied by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<SignalDeliveryResult> DispatchAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default);
}

/// <summary>Default implementation that delegates persistence to <see cref="IFlowSignalStore"/> and
/// re-dispatch to <see cref="IStepDispatcher.ScheduleStepAsync"/>.</summary>
public sealed class FlowSignalDispatcher : IFlowSignalDispatcher
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromMilliseconds(500);

    private readonly IFlowSignalStore _signalStore;
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRepository _flowRepository;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;

    /// <summary>Initialises the dispatcher with its dependencies.</summary>
    public FlowSignalDispatcher(
        IFlowSignalStore signalStore,
        IFlowRunStore runStore,
        IFlowRepository flowRepository,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository)
    {
        _signalStore = signalStore;
        _runStore = runStore;
        _flowRepository = flowRepository;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
    }

    /// <inheritdoc/>
    public async ValueTask<SignalDeliveryResult> DispatchAsync(
        Guid runId,
        string signalName,
        string payloadJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signalName))
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null);
        }

        var result = await _signalStore.DeliverSignalAsync(runId, signalName.Trim(), payloadJson, ct).ConfigureAwait(false);
        if (result.Status != SignalDeliveryStatus.Delivered || result.StepKey is null)
        {
            return result;
        }

        var run = await _runStore.GetRunDetailAsync(runId).ConfigureAwait(false);
        if (run is null)
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null);
        }

        var flows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flow = flows.FirstOrDefault(f => f.Id == run.FlowId);
        if (flow is null)
        {
            return result;
        }

        var stepMeta = flow.Manifest.Steps.FindStep(result.StepKey);
        if (stepMeta is null)
        {
            return result;
        }

        var ctx = new ExecutionContext { RunId = runId };
        ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
        ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(runId).ConfigureAwait(false);

        var step = new StepInstance(result.StepKey, stepMeta.Type)
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow + ResumeDelay,
            Inputs = new Dictionary<string, object?>(stepMeta.Inputs)
        };

        // Best-effort: a 500ms delay lets the prior Pending invocation release its dispatch claim
        // before the resume attempt re-acquires it. We swallow dispatcher errors and surface the
        // delivery status — the timeout safety-net invocation will catch up either way.
        try
        {
            await _dispatcher.ScheduleStepAsync(ctx, flow, step, ResumeDelay, ct).ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed; signal is still delivered, handler will observe on next dispatch.
        }

        return result;
    }
}
