using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Hosting.Internal;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Hosted service that re-enqueues stuck flow runs on application startup.
/// </summary>
/// <remarks>
/// <para>
/// Runs once during <see cref="StartAsync"/>. For each active (Running) run, evaluates the DAG
/// and re-dispatches any ready step whose dispatch record is missing — indicating that the previous
/// host crashed between persisting a result and enqueuing the next step.
/// </para>
/// <para>
/// Recovery applies the same schedule-time rules as the engine's graph continuation before it dispatches:
/// a ready step whose <c>When</c> clause is false is recorded <c>Skipped</c> (with its evaluation trace), a
/// step whose prerequisites finished in an unaccepted status is recorded skipped, and the pass repeats until
/// it writes nothing. A dispatch that throws is logged and the run's remaining steps are still recovered.
/// </para>
/// <para>
/// Two layers of safety prevent duplicate execution even when multiple replicas start simultaneously:
/// <list type="number">
///   <item><see cref="IFlowRunStore.TryRecordDispatchAsync"/> — atomic INSERT that only one replica wins.</item>
///   <item><see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/> — atomic claim that only one worker executes.</item>
/// </list>
/// </para>
/// <para>
/// Requires <see cref="IFlowRunRuntimeStore"/> to evaluate step statuses; construction fails without one.
/// </para>
/// <para>
/// Per-run recovery is delegated to <see cref="RunRecoverer"/>; this class owns only the
/// startup loop and active-run iteration.
/// </para>
/// </remarks>
internal sealed class FlowRunRecoveryHostedService : IHostedService
{
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRunRuntimeStore _runtimeStore;
    private readonly FlowRegistry _flows;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger<FlowRunRecoveryHostedService> _logger;
    private readonly TimeSpan? _orphanedClaimTimeout;

    /// <summary>Initialises the recovery service with its dependencies.</summary>
    /// <param name="runStore">Run store the active runs and the dispatch ledger are read from.</param>
    /// <param name="runtimeStores">Runtime stores; the first one is used.</param>
    /// <param name="flows">Registered flow definitions.</param>
    /// <param name="graphPlanner">Planner that classifies ready steps.</param>
    /// <param name="dispatcher">Runtime adapter recovered steps are handed to.</param>
    /// <param name="outputsRepository">Outputs repository (trigger data, loop iteration counts).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="runControlOptions">
    /// Supplies <see cref="FlowRunControlOptions.OrphanedClaimTimeout"/>; when <see langword="null"/> the
    /// default timeout applies.
    /// </param>
    public FlowRunRecoveryHostedService(
        IFlowRunStore runStore,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        FlowRegistry flows,
        IFlowGraphPlanner graphPlanner,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger<FlowRunRecoveryHostedService> logger,
        FlowRunControlOptions? runControlOptions = null)
    {
        _runStore = runStore;
        _runtimeStore = RequiredStore.First(runtimeStores, nameof(FlowRunRecoveryHostedService));
        _flows = flows;
        _graphPlanner = graphPlanner;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
        _orphanedClaimTimeout = runControlOptions is null
            ? FlowRunControlOptions.DefaultOrphanedClaimTimeout
            : runControlOptions.OrphanedClaimTimeout;
    }

    /// <summary>Scans active runs and re-dispatches any orphaned steps.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FlowRunRecord> activeRuns;
        try
        {
            activeRuns = await _runStore.GetActiveRunsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlowRunRecoveryHostedService: could not query active runs — recovery skipped.");
            return;
        }

        if (activeRuns.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "FlowRunRecoveryHostedService: found {Count} active run(s) to evaluate for recovery.", activeRuns.Count);

        // The same evaluator the engine builds: recovery is a scheduling path, so a ready step's When clause
        // must be applied before it is dispatched (RunStepAsync never re-checks it).
        var whenGate = new WhenSkipGate(new WhenClauseEvaluator(_outputsRepository, _runStore), _runtimeStore, _logger);
        var recoverer = new RunRecoverer(
            _runStore,
            _runtimeStore,
            _graphPlanner,
            _dispatcher,
            _outputsRepository,
            whenGate,
            _logger,
            _orphanedClaimTimeout);

        foreach (var run in activeRuns)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_flows.TryGetFlow(run.FlowId, out var flow))
            {
                _logger.LogWarning(
                    "FlowRunRecoveryHostedService: run {RunId} references unknown flow {FlowId} — skipping.",
                    run.Id, run.FlowId);
                continue;
            }

            try
            {
                await recoverer.RecoverRunAsync(run, flow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "FlowRunRecoveryHostedService: error recovering run {RunId} — continuing with next run.", run.Id);
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
