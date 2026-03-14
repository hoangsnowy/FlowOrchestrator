using System.Text.Json;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Hangfire;

internal sealed class FlowSyncHostedService : IHostedService
{
    private readonly IFlowRepository _repository;
    private readonly IFlowStore _store;
    private readonly ILogger<FlowSyncHostedService> _logger;

    public FlowSyncHostedService(IFlowRepository repository, IFlowStore store, ILogger<FlowSyncHostedService> logger)
    {
        _repository = repository;
        _store = store;
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync flow {FlowId}.", flow.Id);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
