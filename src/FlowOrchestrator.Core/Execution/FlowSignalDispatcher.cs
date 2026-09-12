using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

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
/// re-dispatch to <see cref="IStepDispatcher"/>.</summary>
/// <remarks>
/// The resume nudge is dispatched immediately (<see cref="IStepDispatcher.EnqueueStepAsync"/>) whenever
/// the parked step no longer holds an execution claim, and only falls back to a short delayed dispatch
/// in the narrow window where it still does. This matters because runtimes route delayed work
/// differently from immediate work: on Hangfire any non-zero delay becomes <c>BackgroundJob.Schedule</c>,
/// which parks the job in the Scheduled set until the next <c>DelayedJobScheduler</c> tick
/// (<c>BackgroundJobServerOptions.SchedulePollingInterval</c>, 15 seconds by default). Always delaying
/// therefore turned a 500 ms intent into up to 15 seconds of observed resume latency.
/// </remarks>
public sealed class FlowSignalDispatcher : IFlowSignalDispatcher
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromMilliseconds(500);

    private readonly IFlowSignalStore _signalStore;
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRepository _flowRepository;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly FlowOrchestratorTelemetry? _telemetry;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly ILogger<FlowSignalDispatcher>? _logger;

    /// <summary>Initialises the dispatcher with its dependencies.</summary>
    /// <param name="signalStore">Persistence for signal waiters and delivered payloads.</param>
    /// <param name="runStore">Used to resolve the run that owns the addressed waiter.</param>
    /// <param name="flowRepository">Used to resolve the flow definition backing the run.</param>
    /// <param name="dispatcher">Runtime adapter that carries the resume nudge back to a worker.</param>
    /// <param name="outputsRepository">Supplies trigger data/headers for the rebuilt execution context.</param>
    /// <param name="telemetry">Optional — when omitted, signal-wait metrics are not emitted.</param>
    /// <param name="runtimeStores">
    /// Registered runtime stores; the first is used, matching how <see cref="FlowOrchestratorEngine"/>
    /// selects its own store. Taking the sequence rather than a single service keeps the two in
    /// agreement when a consumer layers a decorator registration on top of the built-in one — a
    /// single-service resolution would bind to the LAST registration while the engine binds to the
    /// FIRST, and the dispatcher would then read claim state from a different store than the engine
    /// claims against.
    /// </param>
    /// <param name="logger">Optional — when omitted, lost resume nudges are not reported anywhere.</param>
    public FlowSignalDispatcher(
        IFlowSignalStore signalStore,
        IFlowRunStore runStore,
        IFlowRepository flowRepository,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        FlowOrchestratorTelemetry? telemetry = null,
        IEnumerable<IFlowRunRuntimeStore>? runtimeStores = null,
        ILogger<FlowSignalDispatcher>? logger = null)
    {
        _signalStore = signalStore;
        _runStore = runStore;
        _flowRepository = flowRepository;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _telemetry = telemetry;
        _runtimeStore = runtimeStores?.FirstOrDefault();
        _logger = logger;
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

        // Header only — the sole field read below is run.FlowId. GetRunDetailAsync would issue three
        // queries and pull every step and attempt row with their unbounded JSON columns, on a path
        // that runs once per signal delivery.
        var run = await _runStore.GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
        {
            return new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null);
        }

        // Record the parked-step wait time: from waiter registration to signal delivery.
        // GetWaiterAsync returns the persisted row including CreatedAt; DeliveredAt is set
        // by DeliverSignalAsync above. Skip silently if telemetry was not registered.
        if (_telemetry is not null && result.DeliveredAt is { } deliveredAt)
        {
            var waiter = await _signalStore.GetWaiterAsync(runId, result.StepKey, ct).ConfigureAwait(false);
            if (waiter is not null)
            {
                var waitMs = Math.Max(0, (deliveredAt - waiter.CreatedAt).TotalMilliseconds);
                _telemetry.SignalWaitMs.Record(
                    waitMs,
                    new KeyValuePair<string, object?>("flow_id", run.FlowId.ToString()),
                    new KeyValuePair<string, object?>("step_key", result.StepKey),
                    new KeyValuePair<string, object?>("signal_name", signalName));
            }
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

        var mustDelay = await RequiresDelayedResumeAsync(runId, result.StepKey).ConfigureAwait(false);

        var step = new StepInstance(result.StepKey, stepMeta.Type)
        {
            RunId = runId,
            ScheduledTime = mustDelay ? DateTimeOffset.UtcNow + ResumeDelay : DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(stepMeta.Inputs)
        };

        // The nudge deliberately runs with CancellationToken.None rather than the caller's token.
        // The signal is already durably delivered by this point, so the resume must survive the
        // request that delivered it: the dashboard endpoint passes http.RequestAborted, which trips
        // the moment the caller disconnects or the response flushes. InMemoryStepDispatcher drops the
        // token inside ScheduleStepAsync for exactly this reason (the v1.26.1 "WaitForSignal never
        // resumes" regression), but EnqueueStepAsync honours it — a cancelled token there fails the
        // channel write and strands the step until its safety-net invocation, up to 24 hours away
        // when the step declares no timeout. Dropping the token here covers every dispatcher.
        try
        {
            if (mustDelay)
            {
                await _dispatcher.ScheduleStepAsync(ctx, flow, step, ResumeDelay, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None).ConfigureAwait(false);
            }
        }
        // Filtered rather than a bare catch-all: the nudge is dispatched with CancellationToken.None,
        // so an OperationCanceledException here is not a caller walking away — it is a genuine fault
        // in the runtime adapter, and swallowing it would hide the one case worth surfacing.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: the delivery itself succeeded, so the caller is told Delivered either way.
            // Log loudly — the blast radius of a lost nudge is a step parked until its safety net,
            // which is 24 hours when no timeoutSeconds is configured.
            SignalLog.ResumeDispatchFailed(_logger, ex, runId, result.StepKey, mustDelay);
        }

        return result;
    }

    /// <summary>
    /// Decides whether the resume nudge must be delayed instead of enqueued immediately.
    /// </summary>
    /// <param name="runId">The run owning the parked step.</param>
    /// <param name="stepKey">Key of the step being resumed.</param>
    /// <returns>
    /// <see langword="true"/> when the invocation that parked the step still holds its execution
    /// claim, or when claim state cannot be read; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The step registers its waiter — which is what makes a delivery possible at all — a few
    /// milliseconds before the engine releases the execution claim on the Pending path. A resume
    /// enqueued inside that window loses <see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/> and is
    /// dropped silently, stranding the step until its parked safety-net invocation fires. Detecting
    /// the window costs one claim read and lets every other delivery — the overwhelming majority,
    /// where the step has been parked for seconds or longer — resume immediately.
    /// </remarks>
    private async ValueTask<bool> RequiresDelayedResumeAsync(Guid runId, string stepKey)
    {
        // No runtime store means the ENGINE has none either — both resolve the same registration —
        // and FlowOrchestratorEngine.RunStepAsync only claims when its own store is non-null. With no
        // claim guard in play there is no TryClaimStepAsync for the resume to lose, so the race this
        // delay exists to dodge cannot happen and the immediate path is provably safe. Delaying here
        // would impose the full Hangfire scheduled-set penalty on exactly the configuration that
        // cannot suffer the race.
        if (_runtimeStore is null)
        {
            return false;
        }

        try
        {
            // Point lookup, not an enumeration: claims are never released on terminal statuses, so
            // the run's claim set grows with every executed step and would be O(n) per delivery.
            return await _runtimeStore.IsStepClaimedAsync(runId, stepKey).ConfigureAwait(false);
        }
        // Filtered for the same reason as the dispatch catch above: this read takes no cancellation
        // token, so an OperationCanceledException would signal a fault worth propagating rather than
        // a caller that gave up.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable claim state is not worth racing over, so take the safe path — but say so.
            // A persistently failing claim store would otherwise silently reinstate the very latency
            // this class exists to remove, indistinguishable from the original bug.
            SignalLog.ClaimStateUnreadable(_logger, ex, runId, stepKey);
            return true;
        }
    }
}
