using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Fluent builder returned by <c>AddFlowOrchestrator()</c> for configuring
/// storage backends, flows, and optional features (Hangfire, scheduler, retention, observability).
/// </summary>
public sealed class FlowOrchestratorBuilder
{
    /// <summary>Initialises the builder with the application's service collection.</summary>
    public FlowOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection to register services into.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// <see langword="true"/> when <see cref="UseHangfire"/> has been called.
    /// Checked by <c>AddFlowOrchestrator</c> to conditionally register Hangfire services.
    /// </summary>
    internal bool HangfireEnabled { get; private set; }

    /// <summary>Scheduler options controlling cron override persistence behaviour.</summary>
    public FlowSchedulerOptions Scheduler { get; } = new();

    /// <summary>Run-control options for timeout and idempotency enforcement.</summary>
    public FlowRunControlOptions RunControl { get; } = new();

    /// <summary>Retention options for the background data cleanup sweep.</summary>
    public FlowRetentionOptions Retention { get; } = new();

    /// <summary>Observability options for OpenTelemetry and event persistence.</summary>
    public FlowObservabilityOptions Observability { get; } = new();

    /// <summary>
    /// Activates Hangfire integration: registers <see cref="IHangfireFlowTrigger"/>,
    /// <see cref="IHangfireStepRunner"/>, and the graph planner.
    /// </summary>
    public FlowOrchestratorBuilder UseHangfire()
    {
        HangfireEnabled = true;
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
