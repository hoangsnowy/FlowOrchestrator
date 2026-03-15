using System.Text.Json;
using FlowOrchestrator.Core.Storage;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

internal sealed class FlowSyncHostedService : IHostedService
{
    private readonly IFlowRepository _repository;
    private readonly IFlowStore _store;
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly ILogger<FlowSyncHostedService> _logger;

    public FlowSyncHostedService(
        IFlowRepository repository,
        IFlowStore store,
        IRecurringJobManager recurringJobManager,
        ILogger<FlowSyncHostedService> logger)
    {
        _repository = repository;
        _store = store;
        _recurringJobManager = recurringJobManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var flows = await _repository.GetAllFlowsAsync().ConfigureAwait(false);
        foreach (var flow in flows)
        {
            try
            {
                var manifestJson = JsonSerializer.Serialize(flow.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var existing = await _store.GetByIdAsync(flow.Id).ConfigureAwait(false);
                var record = existing ?? new FlowDefinitionRecord { Id = flow.Id };
                record.Name = flow.GetType().Name;
                record.Version = flow.Version;
                record.ManifestJson = manifestJson;
                await _store.SaveAsync(record).ConfigureAwait(false);
                _logger.LogInformation("Synced flow {FlowName} ({FlowId}) to store.", record.Name, record.Id);

                SyncRecurringTriggers(flow, record.IsEnabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync flow {FlowId}.", flow.Id);
            }
        }
    }

    internal void SyncRecurringTriggers(Core.Abstractions.IFlowDefinition flow, bool isEnabled)
    {
        foreach (var (triggerKey, trigger) in flow.Manifest.Triggers)
        {
            var jobId = $"flow-{flow.Id}-{triggerKey}";

            if (!string.Equals(trigger.Type, "Cron", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!isEnabled
                || !trigger.Inputs.TryGetValue("cronExpression", out var cronObj)
                || cronObj is not string cronExpression
                || string.IsNullOrWhiteSpace(cronExpression))
            {
                _recurringJobManager.RemoveIfExists(jobId);
                _logger.LogInformation("Removed recurring job {JobId} (disabled or missing cron).", jobId);
                continue;
            }

            var flowId = flow.Id;
            var key = triggerKey;
            _recurringJobManager.AddOrUpdate<IHangfireFlowTrigger>(
                jobId,
                t => t.TriggerByScheduleAsync(flowId, key, null),
                cronExpression);

            _logger.LogInformation(
                "Registered recurring job {JobId} for flow {FlowId} trigger '{TriggerKey}' with cron '{Cron}'.",
                jobId, flow.Id, triggerKey, cronExpression);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
