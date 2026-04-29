using System.Text.Json;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hosted service that runs at application startup to validate, upsert, and
/// activate all code-registered flow definitions, then delegates recurring-job
/// synchronisation to the runtime-specific <see cref="IRecurringTriggerSync"/>.
/// </summary>
/// <remarks>
/// The recurring-trigger logic itself is runtime-agnostic — Hangfire provides
/// <c>RecurringTriggerSync</c>, the in-memory runtime provides
/// <c>PeriodicTimerRecurringTriggerDispatcher</c>. This service does not
/// depend on Hangfire types directly.
/// </remarks>
internal sealed class FlowSyncHostedService : IHostedService
{
    private readonly IFlowRepository _repository;
    private readonly IFlowStore _store;
    private readonly IRecurringTriggerSync _triggerSync;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly ILogger<FlowSyncHostedService> _logger;

    /// <summary>Initialises the hosted service with required services.</summary>
    public FlowSyncHostedService(
        IFlowRepository repository,
        IFlowStore store,
        IRecurringTriggerSync triggerSync,
        IFlowGraphPlanner graphPlanner,
        ILogger<FlowSyncHostedService> logger)
    {
        _repository = repository;
        _store = store;
        _triggerSync = triggerSync;
        _graphPlanner = graphPlanner;
        _logger = logger;
    }

    /// <inheritdoc/>
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

                _triggerSync.SyncTriggers(flow.Id, record.IsEnabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync flow {FlowId}.", flow.Id);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
