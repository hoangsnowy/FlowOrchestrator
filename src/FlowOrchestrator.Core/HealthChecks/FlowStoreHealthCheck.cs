using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowOrchestrator.Core.HealthChecks;

/// <summary>
/// Health check that probes <see cref="IFlowStore"/> reachability so a load balancer
/// or container orchestrator can refuse traffic when the storage backend is unavailable.
/// </summary>
/// <remarks>
/// The probe issues the cheapest read on every backend
/// (<see cref="IFlowStore.GetAllAsync"/>) and is wrapped in a configurable timeout so
/// a hung connection pool never blocks `/health` for longer than the budget.
/// Returns <see cref="HealthStatus.Healthy"/> with a <c>flow_count</c> data entry on success,
/// <see cref="HealthStatus.Unhealthy"/> on timeout or any thrown exception.
/// </remarks>
public sealed class FlowStoreHealthCheck : IHealthCheck
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IFlowStore _store;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a new health check bound to the given <paramref name="store"/>.</summary>
    /// <param name="store">The flow definition store to probe.</param>
    /// <param name="timeout">
    /// Probe budget; defaults to 5 seconds when <see langword="null"/>.
    /// On expiry the check returns <see cref="HealthStatus.Unhealthy"/>.
    /// </param>
    public FlowStoreHealthCheck(IFlowStore store, TimeSpan? timeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            var flows = await _store.GetAllAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["flow_count"] = flows.Count,
            };
            return HealthCheckResult.Healthy("Flow store is reachable.", data);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"Flow store probe timed out after {_timeout.TotalMilliseconds:N0} ms.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Flow store is unreachable.", ex);
        }
    }
}
