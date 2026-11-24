using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting.Internal;

/// <summary>
/// One pass of the unsent-dispatch sweep: leases dispatch-ledger rows whose hand-off to the runtime never
/// returned (<see cref="IFlowRunStore.LeaseUnsentDispatchesAsync"/>) and hands each step to the runtime
/// again.
/// </summary>
/// <remarks>
/// <para>
/// The ledger row is written before the runtime call, so a process that dies mid-send — or a send that
/// hangs — leaves a row that claims "an attempt is queued" for an attempt the runtime may never have
/// received. Startup recovery skips every ledgered key, so before this sweep such a step never ran again.
/// </para>
/// <para>
/// Not on the step-completion path: one leasing query per tick, then O(1) work per leased row (one trigger
/// read per distinct run, one dispatch, one annotation). The lease is a durable write, so from the moment it
/// returns every row is handed to the runtime with <see cref="CancellationToken.None"/>.
/// </para>
/// </remarks>
internal sealed class UnsentDispatchRecoverer
{
    /// <summary>Rows leased per store call; the pass keeps leasing while a call comes back full.</summary>
    internal const int BatchSize = 100;

    /// <summary>Upper bound on store calls per pass, so one tick cannot run unbounded behind a flood.</summary>
    internal const int MaxBatchesPerPass = 10;

    private readonly IFlowRunStore _runStore;
    private readonly FlowRegistry _flows;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger _logger;

    /// <summary>Initialises the recoverer.</summary>
    /// <param name="runStore">Run store that leases and annotates the ledger rows.</param>
    /// <param name="flows">Registry the leased rows' flow definitions are resolved from.</param>
    /// <param name="dispatcher">Runtime adapter the steps are handed to again.</param>
    /// <param name="outputsRepository">Source of the run's trigger payload and headers.</param>
    /// <param name="logger">Logger of the hosting service.</param>
    public UnsentDispatchRecoverer(
        IFlowRunStore runStore,
        FlowRegistry flows,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger logger)
    {
        _runStore = runStore;
        _flows = flows;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    /// <summary>Leases every qualifying ledger row and re-delivers its step.</summary>
    /// <param name="timeout">Minimum age of an unannotated row; see <c>FlowRunControlOptions.UnsentDispatchTimeout</c>.</param>
    /// <param name="cancellationToken">Stops the pass between batches; never interrupts a leased row's hand-off.</param>
    /// <returns>The number of steps handed to the runtime again.</returns>
    public async Task<int> RecoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var redelivered = 0;
        Dictionary<Guid, IExecutionContext>? contexts = null;

        for (var batch = 0; batch < MaxBatchesPerPass && !cancellationToken.IsCancellationRequested; batch++)
        {
            var leased = await _runStore.LeaseUnsentDispatchesAsync(timeout, BatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var row in leased)
            {
                contexts ??= [];
                if (await RedeliverAsync(row, timeout, contexts, cancellationToken).ConfigureAwait(false))
                {
                    redelivered++;
                }
            }

            if (leased.Count < BatchSize)
            {
                break;
            }
        }

        return redelivered;
    }

    private async Task<bool> RedeliverAsync(
        UnsentDispatch row,
        TimeSpan timeout,
        Dictionary<Guid, IExecutionContext> contexts,
        CancellationToken stoppingToken)
    {
        if (!_flows.TryGetFlow(row.FlowId, out var flow) || flow.Manifest.Steps.FindStep(row.StepKey) is not { } metadata)
        {
            // Another host (a newer deployment, a different flow set) may know it; the lease expires and it
            // qualifies again a window later.
            HostingLog.UnsentDispatchUnknownStep(_logger, row.StepKey, row.RunId, row.FlowId);
            return false;
        }

        if (!contexts.TryGetValue(row.RunId, out var ctx))
        {
            ctx = await BuildContextAsync(row.RunId).ConfigureAwait(false);
            contexts[row.RunId] = ctx;
        }

        var step = new StepInstance(row.StepKey, metadata.Type)
        {
            RunId = row.RunId,
            TriggerData = ctx.TriggerData,
            TriggerHeaders = ctx.TriggerHeaders,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>(metadata.Inputs)
        };

        string? jobId;
        try
        {
            // CancellationToken.None: the lease above is a durable write. The ledger row stays in place — it
            // is the reservation this re-delivery fulfils — so no second row is recorded.
            jobId = await _dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Any failure, cancellation included: no token was passed, so an OperationCanceledException outside
            // shutdown is the adapter's own timeout. The lease re-stamped the row, so it qualifies again one
            // timeout later.
            HostingLog.UnsentDispatchRedeliveryFailed(_logger, ex, row.StepKey, row.RunId);
            return false;
        }

        HostingLog.UnsentDispatchRedelivered(_logger, row.StepKey, row.RunId, timeout.TotalSeconds);

        try
        {
            await _runStore.AnnotateDispatchAsync(row.RunId, row.StepKey, DispatchLedgerMarker.For(jobId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unannotated, the row is leased again one timeout later and re-delivered as a duplicate, which the
            // execution claim absorbs.
            HostingLog.UnsentDispatchAnnotateFailed(_logger, ex, row.StepKey);
        }

        return true;
    }

    private async Task<IExecutionContext> BuildContextAsync(Guid runId)
    {
        var ctx = new Execution.ExecutionContext { RunId = runId };
        try
        {
            ctx.TriggerData = await _outputsRepository.GetTriggerDataAsync(runId).ConfigureAwait(false);
            ctx.TriggerHeaders = await _outputsRepository.GetTriggerHeadersAsync(runId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HostingLog.RecoveryTriggerDataUnavailable(_logger, ex, runId);
        }

        return ctx;
    }
}
