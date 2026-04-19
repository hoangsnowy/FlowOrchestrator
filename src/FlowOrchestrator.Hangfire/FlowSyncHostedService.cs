using System.Text.Json;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hosted service that runs at application startup to validate, upsert, and
/// activate all code-registered flow definitions.
/// Also registers Hangfire recurring jobs for each flow's cron triggers,
/// applying any persisted schedule overrides.
/// </summary>
internal sealed class FlowSyncHostedService : IHostedService
{
    private readonly IFlowRepository _repository;
    private readonly IFlowStore _store;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IFlowScheduleStateStore _scheduleStateStore;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly FlowSchedulerOptions _schedulerOptions;
    private readonly ILogger<FlowSyncHostedService> _logger;

    public FlowSyncHostedService(
        IFlowRepository repository,
        IFlowStore store,
        IRecurringJobManager recurringJobManager,
        IFlowScheduleStateStore scheduleStateStore,
        IFlowGraphPlanner graphPlanner,
        FlowSchedulerOptions schedulerOptions,
        ILogger<FlowSyncHostedService> logger)
    {
        _repository = repository;
        _store = store;
        _recurringJobManager = recurringJobManager;
        _scheduleStateStore = scheduleStateStore;
        _graphPlanner = graphPlanner;
        _schedulerOptions = schedulerOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var flows = await _repository.GetAllFlowsAsync().ConfigureAwait(false);
        foreach (var flow in flows)
        {
            try
            {
                var validation = _graphPlanner.Validate(flow);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Flow '{flow.GetType().Name}' ({flow.Id}) is invalid: {string.Join("; ", validation.Errors)}");
                }

                var manifestJson = JsonSerializer.Serialize(flow.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var existing = await _store.GetByIdAsync(flow.Id).ConfigureAwait(false);
                var record = existing ?? new FlowDefinitionRecord { Id = flow.Id };
                record.Name = flow.GetType().Name;
                record.Version = flow.Version;
                record.ManifestJson = manifestJson;
                await _store.SaveAsync(record).ConfigureAwait(false);
                _logger.LogInformation("Synced flow {FlowName} ({FlowId}) to store.", record.Name, record.Id);

                await SyncRecurringTriggersAsync(flow, record.IsEnabled).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync flow {FlowId}.", flow.Id);
                throw;
            }
        }
    }

    internal async Task SyncRecurringTriggersAsync(Core.Abstractions.IFlowDefinition flow, bool isEnabled)
    {
        foreach (var (triggerKey, trigger) in flow.Manifest.Triggers)
        {
            var jobId = $"flow-{flow.Id}-{triggerKey}";
            var state = _schedulerOptions.PersistOverrides
                ? await _scheduleStateStore.GetAsync(jobId).ConfigureAwait(false)
                : null;

            if (trigger.Type != Core.Abstractions.TriggerType.Cron)
                continue;

            if (!isEnabled
                || !trigger.TryGetCronExpression(out var cronExpression))
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Removed recurring job {JobId} (disabled or missing cron).", jobId);
                continue;
            }

            if (state?.IsPaused == true)
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Recurring job {JobId} remains paused.", jobId);
                continue;
            }

            var effectiveCron = string.IsNullOrWhiteSpace(state?.CronOverride)
                ? cronExpression
                : state!.CronOverride!;

            var flowId = flow.Id;
            var key = triggerKey;
            _recurringJobManager.AddOrUpdate<IHangfireFlowTrigger>(
                jobId,
                t => t.TriggerByScheduleAsync(flowId, key, null),
                effectiveCron);

            _logger.LogInformation(
                "Registered recurring job {JobId} for flow {FlowId} trigger '{TriggerKey}' with cron '{Cron}'.",
                jobId, flow.Id, triggerKey, effectiveCron);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
