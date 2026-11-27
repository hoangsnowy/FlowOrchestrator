using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting.Internal;

/// <summary>
/// One pass of the expired-lease sweep: selects steps whose execution lease expired without a terminal status
/// (<see cref="IFlowRunRuntimeStore.LeaseExpiredStepLeasesAsync"/>) and hands each to the runtime again; the
/// redelivered attempt takes the expired lease over.
/// </summary>
/// <remarks>
/// Not on the step-completion path: one selecting query per batch, then O(1) work per row (one trigger read
/// per distinct run, one dispatch). The selection stamps each row, so from the moment it returns every row is
/// handed to the runtime with <see cref="CancellationToken.None"/>. No dispatch-ledger row is written: the
/// step's original reservation is still in place, and this redelivery fulfils it.
/// </remarks>
internal sealed class ExpiredStepLeaseRecoverer
{
    /// <summary>Rows selected per store call; the pass keeps selecting while a call comes back full.</summary>
    internal const int BatchSize = 100;

    /// <summary>Upper bound on store calls per pass, so one tick cannot run unbounded behind a flood.</summary>
    internal const int MaxBatchesPerPass = 10;

    private readonly IFlowRunRuntimeStore _runtimeStore;
    private readonly FlowRegistry _flows;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger _logger;

    /// <summary>Initialises the recoverer.</summary>
    /// <param name="runtimeStore">Store that selects the expired leases.</param>
    /// <param name="flows">Registry the rows' flow definitions are resolved from.</param>
    /// <param name="dispatcher">Runtime adapter the steps are handed to again.</param>
    /// <param name="outputsRepository">Source of the run's trigger payload and headers.</param>
    /// <param name="logger">Logger of the hosting service.</param>
    public ExpiredStepLeaseRecoverer(
        IFlowRunRuntimeStore runtimeStore,
        FlowRegistry flows,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger logger)
    {
        _runtimeStore = runtimeStore;
        _flows = flows;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    /// <summary>Selects every expired lease and redelivers its step.</summary>
    /// <param name="redeliveryBackoff">Minimum time between two redeliveries of the same expired lease.</param>
    /// <param name="cancellationToken">Stops the pass between batches; never interrupts a selected row's hand-off.</param>
    /// <returns>The number of steps handed to the runtime again.</returns>
    public async Task<int> RecoverAsync(TimeSpan redeliveryBackoff, CancellationToken cancellationToken)
    {
        var redelivered = 0;
        Dictionary<Guid, IExecutionContext>? contexts = null;

        for (var batch = 0; batch < MaxBatchesPerPass && !cancellationToken.IsCancellationRequested; batch++)
        {
            var expired = await _runtimeStore.LeaseExpiredStepLeasesAsync(redeliveryBackoff, BatchSize).ConfigureAwait(false);
            foreach (var row in expired)
            {
                contexts ??= [];
                if (await RedeliverAsync(row, contexts, cancellationToken).ConfigureAwait(false))
                {
                    redelivered++;
                }
            }

            if (expired.Count < BatchSize)
            {
                break;
            }
        }

        return redelivered;
    }

    private async Task<bool> RedeliverAsync(ExpiredStepLease row, Dictionary<Guid, IExecutionContext> contexts, CancellationToken stoppingToken)
    {
        if (!_flows.TryGetFlow(row.FlowId, out var flow) || flow.Manifest.Steps.FindStep(row.StepKey) is not { } metadata)
        {
            // Another host (a newer deployment, a different flow set) may know it; the row qualifies again once
            // the backoff has passed.
            HostingLog.ExpiredLeaseUnknownStep(_logger, row.StepKey, row.RunId, row.FlowId);
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

        try
        {
            // CancellationToken.None: the selection above is a durable write.
            await _dispatcher.EnqueueStepAsync(ctx, flow, step, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Any failure: the row was stamped, so it qualifies again once the backoff has passed.
            HostingLog.ExpiredLeaseRedeliveryFailed(_logger, ex, row.StepKey, row.RunId);
            return false;
        }

        HostingLog.ExpiredLeaseRedelivered(_logger, row.StepKey, row.RunId);
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
