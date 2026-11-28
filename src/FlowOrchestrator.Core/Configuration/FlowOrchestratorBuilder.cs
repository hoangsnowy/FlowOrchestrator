using FlowOrchestrator.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// Fluent builder returned by <c>AddFlowOrchestrator()</c> for configuring
/// storage backends, flows, and optional features (scheduler, retention, observability).
/// </summary>
/// <remarks>
/// Storage backends and runtimes plug in as extension methods from their own packages —
/// <c>UseSqlServer</c> / <c>UsePostgreSql</c> / <c>UseInMemory</c> for storage, and
/// <c>UseHangfire</c> (FlowOrchestrator.Hangfire), <c>UseInMemoryRuntime</c> (FlowOrchestrator.InMemory)
/// or <c>UseAzureServiceBusRuntime</c> (FlowOrchestrator.ServiceBus) for the runtime.
/// </remarks>
public sealed class FlowOrchestratorBuilder
{
    /// <summary>Initialises the builder with the application's service collection.</summary>
    public FlowOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection to register services into.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Scheduler options controlling cron override persistence behaviour.</summary>
    public FlowSchedulerOptions Scheduler { get; } = new();

    /// <summary>Run-control options for timeout and idempotency enforcement.</summary>
    public FlowRunControlOptions RunControl { get; } = new();

    /// <summary>Retention options for the background data cleanup sweep.</summary>
    public FlowRetentionOptions Retention { get; } = new();

    /// <summary>Observability options for OpenTelemetry instrumentation.</summary>
    public FlowObservabilityOptions Observability { get; } = new();

    /// <summary>Signal options for <c>WaitForSignal</c> parking and resume-nudge dispatch.</summary>
    public FlowSignalOptions Signals { get; } = new();

    /// <summary>Server-registry options: heartbeat interval, stale threshold and purge age of the Servers view.</summary>
    public FlowServerOptions Servers { get; } = new();

    /// <summary>Options for the opt-in run-search token index; see <see cref="UseRunSearchIndex"/>.</summary>
    public RunSearchIndexOptions RunSearchIndex { get; } = new();

    /// <summary>
    /// Enables the built-in run-search token index (experimental): deep search by token-prefix index
    /// seek instead of a <c>LIKE</c> scan over step payloads, with a background back-fill of existing runs.
    /// </summary>
    /// <param name="configure">Optional tuning of batch sizes and intervals.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// Off by default. Honoured by the SQL Server provider; other providers ignore it. Read
    /// <see cref="RunSearchIndexOptions"/> for the storage and write-path cost before enabling it.
    /// </remarks>
    public FlowOrchestratorBuilder UseRunSearchIndex(Action<RunSearchIndexOptions>? configure = null)
    {
        RunSearchIndex.Enabled = true;
        configure?.Invoke(RunSearchIndex);
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TFlow"/> as a singleton <see cref="IFlowDefinition"/>.
    /// At startup, <c>FlowSyncHostedService</c> validates and syncs all registered flows.
    /// </summary>
    /// <typeparam name="TFlow">The concrete flow definition class with a default constructor.</typeparam>
    public FlowOrchestratorBuilder AddFlow<TFlow>() where TFlow : class, IFlowDefinition, new()
    {
        Services.AddSingleton<IFlowDefinition, TFlow>();
        return this;
    }
}
