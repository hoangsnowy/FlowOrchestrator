using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowOrchestrator.Core.HealthChecks;

/// <summary>
/// <see cref="IHealthChecksBuilder"/> extensions that register FlowOrchestrator health checks.
/// </summary>
public static class FlowOrchestratorHealthCheckExtensions
{
    /// <summary>Default registration name used by <see cref="AddFlowOrchestratorHealthChecks"/>.</summary>
    public const string DefaultName = "flow-orchestrator-storage";

    /// <summary>
    /// Registers a health check that probes <see cref="IFlowStore"/> reachability.
    /// </summary>
    /// <param name="builder">The health-check builder, typically obtained from <c>services.AddHealthChecks()</c>.</param>
    /// <param name="name">Registration name. Defaults to <see cref="DefaultName"/>.</param>
    /// <param name="failureStatus">Status reported when the probe fails. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags. Defaults to <c>["flow-orchestrator", "storage"]</c>.</param>
    /// <param name="timeout">
    /// Probe budget. Defaults to 5 seconds. On expiry the check reports <paramref name="failureStatus"/>.
    /// </param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <remarks>
    /// The check resolves <see cref="IFlowStore"/> from DI on every probe so the registered
    /// store implementation (SQL Server / PostgreSQL / in-memory) is honoured without re-registration.
    /// </remarks>
    public static IHealthChecksBuilder AddFlowOrchestratorHealthChecks(
        this IHealthChecksBuilder builder,
        string name = DefaultName,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new FlowStoreHealthCheck(
                sp.GetRequiredService<IFlowStore>(),
                timeout),
            failureStatus,
            tags ?? new[] { "flow-orchestrator", "storage" }));
    }
}
