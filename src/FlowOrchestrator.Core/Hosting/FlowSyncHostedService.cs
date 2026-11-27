using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Hosted service that runs at application startup to validate, upsert, and
/// activate all code-registered flow definitions, then synchronises their cron triggers
/// through <see cref="IRecurringTriggerSync"/>.
/// </summary>
/// <remarks>
/// Runtime-agnostic: the recurring-job mechanics belong to the configured runtime's
/// <see cref="IRecurringTriggerDispatcher"/> (Hangfire, in-memory timer, Service Bus), reached
/// through <see cref="RecurringTriggerSync"/>. A flow that fails validation stops host start-up.
/// </remarks>
internal sealed class FlowSyncHostedService : IHostedService
{
    private readonly FlowRegistry _flows;
    private readonly IFlowStore _store;
    private readonly IRecurringTriggerSync _triggerSync;
    private readonly IFlowGraphPlanner _graphPlanner;
    private readonly ILogger<FlowSyncHostedService> _logger;

    /// <summary>Initialises the hosted service with required services.</summary>
    public FlowSyncHostedService(
        FlowRegistry flows,
        IFlowStore store,
        IRecurringTriggerSync triggerSync,
        IFlowGraphPlanner graphPlanner,
        ILogger<FlowSyncHostedService> logger)
    {
        _flows = flows;
        _store = store;
        _triggerSync = triggerSync;
        _graphPlanner = graphPlanner;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var flow in _flows.Flows)
        {
            try
            {
                var validation = _graphPlanner.Validate(flow);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Flow '{flow.GetType().Name}' ({flow.Id}) is invalid: {string.Join("; ", validation.Errors)}");
                }

                // The same serialization the engine compares against when a worker meets a step its manifest
                // does not contain (RegisteredManifest.Matches) — one writer, one reader.
                var manifestJson = RegisteredManifest.Serialize(flow);
                var existing = await _store.GetByIdAsync(flow.Id).ConfigureAwait(false);
                var record = existing ?? new FlowDefinitionRecord { Id = flow.Id };
                record.Name = flow.GetType().Name;
                record.Version = flow.Version;
                record.ManifestJson = manifestJson;
                await _store.SaveAsync(record).ConfigureAwait(false);
                HostingLog.FlowSynced(_logger, record.Name, record.Id);

                await _triggerSync.SyncTriggersAsync(flow.Id, record.IsEnabled, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostingLog.FlowSyncFailed(_logger, ex, flow.Id);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
