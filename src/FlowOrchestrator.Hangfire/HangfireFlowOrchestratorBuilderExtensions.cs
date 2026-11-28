using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire.Telemetry;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Registers Hangfire as the FlowOrchestrator runtime: <c>options.UseHangfire()</c> inside
/// <c>AddFlowOrchestrator(options =&gt; …)</c>.
/// </summary>
public static class HangfireFlowOrchestratorBuilderExtensions
{
    /// <summary>
    /// Selects Hangfire as the step-execution and cron runtime: steps become Hangfire background
    /// jobs and cron triggers become Hangfire recurring jobs.
    /// </summary>
    /// <remarks>
    /// Hangfire itself is not configured here — register it as usual with
    /// <c>services.AddHangfire(…)</c> and <c>services.AddHangfireServer()</c>, which provide the
    /// <see cref="IBackgroundJobClient"/> and <see cref="IRecurringJobManager"/> the adapters use.
    /// <para>
    /// The runtime services are added with <c>TryAdd</c>, so if another runtime was already selected
    /// earlier in the same callback that runtime keeps the dispatcher slots. Also installs
    /// <see cref="TraceContextHangfireFilter"/> into <see cref="GlobalJobFilters"/> once per process so
    /// W3C trace context crosses the enqueue / perform boundary.
    /// </para>
    /// <para>
    /// On Hangfire.PostgreSql job storage, a hosted check logs a warning at startup when
    /// <c>DistributedLockTimeout</c> is longer than <c>InvisibilityTimeout</c>: that storage's locks
    /// outlive a killed process, so a step whose worker died mid state change waits for the lock
    /// timeout before it runs again.
    /// </para>
    /// </remarks>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    /// <returns>The same builder for chaining.</returns>
    public static FlowOrchestratorBuilder UseHangfire(this FlowOrchestratorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.TryAddSingleton<IStepDispatcher, HangfireStepDispatcher>();
        builder.Servers.RuntimeName ??= "Hangfire"; // the first runtime selected keeps the dispatcher slot, and the name
        services.TryAddSingleton<IRecurringTriggerDispatcher, HangfireRecurringTriggerDispatcher>();
        services.TryAddSingleton<IRecurringTriggerInspector, HangfireRecurringTriggerInspector>();

        // Job targets Hangfire activates on the worker. TryAdd keeps a repeated UseHangfire() idempotent.
        services.TryAddTransient<IHangfireFlowTrigger, HangfireFlowOrchestrator>();
        services.TryAddTransient<IHangfireStepRunner, HangfireFlowOrchestrator>();

        // Startup warning for a Hangfire.PostgreSql lock timeout that outlives its invisibility timeout,
        // which turns crash recovery into a lock-timeout wait. TryAddEnumerable: one check per host.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HangfirePostgreSqlLockTimeoutCheck>());

        // W3C trace-context propagation across the enqueue / dequeue boundary. Idempotent —
        // registering twice does not stack the filter. The GlobalJobFilters.Filters collection wraps
        // each instance in a JobFilter, so probe via .Instance rather than OfType<T>.
        if (!GlobalJobFilters.Filters.Any(f => f.Instance is TraceContextHangfireFilter))
        {
            GlobalJobFilters.Filters.Add(new TraceContextHangfireFilter());
        }

        return builder;
    }
}
