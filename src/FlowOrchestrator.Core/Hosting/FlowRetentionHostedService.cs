using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Background service that periodically sweeps all registered <see cref="IFlowRetentionStore"/>
/// implementations, deleting run records (steps, outputs, events) older than the configured
/// <see cref="FlowRetentionOptions.DataTtl"/>. Only runs when <see cref="FlowRetentionOptions.Enabled"/> is true.
/// </summary>
internal sealed class FlowRetentionHostedService : BackgroundService
{
    private readonly IReadOnlyList<IFlowRetentionStore> _stores;
    private readonly FlowRetentionOptions _options;
    private readonly ILogger<FlowRetentionHostedService> _logger;

    /// <summary>Initialises the sweep with every registered retention store and the retention options.</summary>
    public FlowRetentionHostedService(
        IEnumerable<IFlowRetentionStore> stores,
        FlowRetentionOptions options,
        ILogger<FlowRetentionHostedService> logger)
    {
        _stores = stores.ToList();
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _stores.Count == 0)
        {
            return;
        }

        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - _options.DataTtl;
        foreach (var store in _stores)
        {
            try
            {
                await store.CleanupAsync(cutoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                HostingLog.RetentionCleanupFailed(_logger, ex, store.GetType().Name);
            }
        }
    }
}

