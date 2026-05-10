using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
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
/// Two layers of safety prevent duplicate execution even when multiple replicas start simultaneously:
/// <list type="number">
///   <item><see cref="IFlowRunStore.TryRecordDispatchAsync"/> — atomic INSERT that only one replica wins.</item>
///   <item><see cref="IFlowRunRuntimeStore.TryClaimStepAsync"/> — atomic claim that only one worker executes.</item>
/// </list>
/// </para>
/// <para>
/// Requires <see cref="IFlowRunRuntimeStore"/> to evaluate step statuses.
/// When no runtime store is registered (legacy sequential mode) recovery is skipped.
/// </para>
/// <para>
/// Per-run recovery is delegated to <see cref="RunRecoverer"/>; this class owns only the
/// startup loop and active-run iteration.
/// </para>
/// </remarks>
public sealed class FlowRunRecoveryHostedService : IHostedService
{
    private readonly IFlowRunStore _runStore;
    private readonly IFlowRunRuntimeStore? _runtimeStore;
    private readonly IFlowRepository _flowRepository;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly IStepDispatcher _dispatcher;
    private readonly IOutputsRepository _outputsRepository;
    private readonly ILogger<FlowRunRecoveryHostedService> _logger;

    /// <summary>Initialises the recovery service with its dependencies.</summary>
    public FlowRunRecoveryHostedService(
        IFlowRunStore runStore,
        IEnumerable<IFlowRunRuntimeStore> runtimeStores,
        IFlowRepository flowRepository,
        IFlowGraphPlanner graphPlanner,
        IStepDispatcher dispatcher,
        IOutputsRepository outputsRepository,
        ILogger<FlowRunRecoveryHostedService> logger)
    {
        _runStore = runStore;
        _runtimeStore = runtimeStores.FirstOrDefault();
        _flowRepository = flowRepository;
        _graphPlanner = graphPlanner;
        _dispatcher = dispatcher;
        _outputsRepository = outputsRepository;
        _logger = logger;
    }

    /// <summary>Scans active runs and re-dispatches any orphaned steps.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtimeStore is null)
        {
            _logger.LogDebug(
                "FlowRunRecoveryHostedService: no IFlowRunRuntimeStore registered — recovery skipped in legacy sequential mode.");
            return;
        }

        IReadOnlyList<FlowRunRecord> activeRuns;
        try
        {
            activeRuns = await _runStore.GetActiveRunsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
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

        var allFlows = await _flowRepository.GetAllFlowsAsync().ConfigureAwait(false);
        var flowMap = allFlows.ToDictionary(f => f.Id);

        var recoverer = new RunRecoverer(
            _runStore,
            _runtimeStore,
            _graphPlanner,
            _dispatcher,
            _outputsRepository,
            _logger);

        foreach (var run in activeRuns)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!flowMap.TryGetValue(run.FlowId, out var flow))
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FlowRunRecoveryHostedService: error recovering run {RunId} — continuing with next run.", run.Id);
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
