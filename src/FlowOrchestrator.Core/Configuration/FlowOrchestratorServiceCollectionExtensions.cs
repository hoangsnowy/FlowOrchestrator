using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Lives in the DI namespace (the Microsoft.Extensions convention) so AddFlowOrchestrator is in scope
// for every runtime without a FlowOrchestrator.* using — before 2.0 it shipped in the Hangfire
// package, which forced that package onto in-memory and Service Bus hosts.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI entry point for FlowOrchestrator: <see cref="AddFlowOrchestrator"/>.
/// </summary>
public static class FlowOrchestratorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runtime-agnostic FlowOrchestrator services — execution engine, graph planner,
    /// flow registry, flow/cron sync, run recovery, timeout enforcement, retention sweep and
    /// observability — then validates that a storage backend and a runtime were configured.
    /// </summary>
    /// <remarks>
    /// Inside <paramref name="configure"/>, pick exactly one storage backend and one runtime:
    /// <list type="bullet">
    ///   <item>storage — <c>UseSqlServer()</c>, <c>UsePostgreSql()</c> or <c>UseInMemory()</c>;</item>
    ///   <item>runtime — <c>UseHangfire()</c> (package <c>FlowOrchestrator.Hangfire</c>; register Hangfire
    ///         itself with <c>AddHangfire</c>/<c>AddHangfireServer</c>), <c>UseInMemoryRuntime()</c>
    ///         (package <c>FlowOrchestrator.InMemory</c>) or <c>UseAzureServiceBusRuntime()</c>
    ///         (package <c>FlowOrchestrator.ServiceBus</c>).</item>
    /// </list>
    /// A custom runtime may instead register its own <see cref="IStepDispatcher"/>,
    /// <see cref="IRecurringTriggerDispatcher"/> and <see cref="IRecurringTriggerInspector"/>.
    /// <para>
    /// Defaults registered with <c>TryAdd</c> after the callback (a backend's own registration wins):
    /// <see cref="InMemoryFlowScheduleStateStore"/>, <see cref="NoopFlowEventNotifier"/> and
    /// <see cref="RecurringTriggerSync"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">Callback that receives a <see cref="FlowOrchestratorBuilder"/> for configuration.</param>
    /// <returns>The configured builder, allowing further chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no <see cref="IFlowStore"/> (storage backend), no <see cref="IStepDispatcher"/>
    /// (runtime) or no <see cref="IRecurringTriggerDispatcher"/> was registered inside <paramref name="configure"/>.
    /// </exception>
    public static FlowOrchestratorBuilder AddFlowOrchestrator(
        this IServiceCollection services,
        Action<FlowOrchestratorBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new FlowOrchestratorBuilder(services);
        configure(builder);

        if (!IsRegistered<IFlowStore>(services))
        {
            throw new InvalidOperationException(
                "No FlowOrchestrator storage backend registered. " +
                "Call options.UseSqlServer(), options.UsePostgreSql(), or options.UseInMemory() " +
                "inside AddFlowOrchestrator(options => ...).");
        }

        if (!IsRegistered<IStepDispatcher>(services))
        {
            throw new InvalidOperationException(
                "No FlowOrchestrator runtime registered. Call options.UseHangfire() (package FlowOrchestrator.Hangfire), " +
                "options.UseInMemoryRuntime() (package FlowOrchestrator.InMemory), or " +
                "options.UseAzureServiceBusRuntime(...) (package FlowOrchestrator.ServiceBus) " +
                "inside AddFlowOrchestrator(options => ...). A custom IStepDispatcher must be registered before this " +
                "check runs: on options.Services inside the callback, or on the service collection before AddFlowOrchestrator().");
        }

        if (!IsRegistered<IRecurringTriggerDispatcher>(services))
        {
            throw new InvalidOperationException(
                "The configured FlowOrchestrator runtime registered an IStepDispatcher but no IRecurringTriggerDispatcher. " +
                "A custom runtime must register IRecurringTriggerDispatcher and IRecurringTriggerInspector as well " +
                "(inside the callback or before AddFlowOrchestrator()); cron triggers are synchronised through them at startup.");
        }

        services.AddScoped<IExecutionContextAccessor, ExecutionContextAccessor>();
        services.AddSingleton<FlowOrchestratorTelemetry>();
        services.AddSingleton(builder.Scheduler);
        services.AddSingleton(builder.RunControl);
        services.AddSingleton(builder.Retention);
        services.AddSingleton(builder.Observability);
        services.AddSingleton(builder.Signals);
        services.AddSingleton(builder.RunSearchIndex);
        builder.Servers.Validate();
        services.AddSingleton(builder.Servers);

        // The one flow registry, built once from every AddFlow<T>() registration on first resolve.
        services.TryAddSingleton(sp => new FlowRegistry(sp.GetServices<IFlowDefinition>()));

        // Process-local fallback; SQL Server / PostgreSQL register a durable store inside configure.
        services.TryAddSingleton<IFlowScheduleStateStore, InMemoryFlowScheduleStateStore>();

        // Default no-op notifier so the engine can always resolve IFlowEventNotifier from DI.
        // Realtime consumers (e.g. the dashboard SSE broadcaster) replace it with services.Replace().
        services.TryAddSingleton<IFlowEventNotifier, NoopFlowEventNotifier>();

        // One effective-cron sync for every runtime; the runtime supplies IRecurringTriggerDispatcher.
        services.TryAddSingleton<IRecurringTriggerSync, RecurringTriggerSync>();

        services.AddHostedService<FlowSyncHostedService>();
        services.AddHostedService<FlowRetentionHostedService>();
        services.AddHostedService<FlowRunRecoveryHostedService>();
        services.AddHostedService<FlowTimeoutEnforcementHostedService>();
        // Re-delivers steps whose ledger row was written but whose runtime hand-off never returned
        // (process died mid-send, or the send hung). No-op on a store without LeaseUnsentDispatchesAsync.
        services.AddHostedService<FlowUnsentDispatchRecoveryHostedService>();
        // Step execution leases (RFC 2.0 C1): the keeper renews the leases of steps executing here, and the
        // sweep redelivers steps whose lease expired because the worker running them died.
        services.TryAddSingleton<StepLeaseKeeper>();
        services.AddHostedService<FlowStepLeaseHostedService>();
        // Server registry: this process's heartbeat row for the dashboard's Servers view. No-op without an
        // IFlowServerStore (a custom storage backend) or with Servers.Enabled = false.
        services.AddHostedService<FlowServerHeartbeatHostedService>();

        services.AddSingleton<IFlowGraphPlanner, FlowGraphPlanner>();
        services.AddTransient<IStepExecutor, DefaultStepExecutor>();

        services.AddTransient<IFlowOrchestrator, FlowOrchestratorEngine>();
        // Same engine instance type also drives the periodic timeout sweep. Resolved per-tick inside
        // a scope by FlowTimeoutEnforcementHostedService (the engine has scoped dependencies).
        services.AddTransient<IRunTimeoutEnforcer, FlowOrchestratorEngine>();

        services.AddStepHandler<ForEachStepHandler>("ForEach");
        services.AddStepHandler<WaitForSignalHandler>(WaitForSignalHandler.StepTypeName);
        services.TryAddSingleton<IFlowSignalDispatcher, FlowSignalDispatcher>();

        return builder;
    }

    private static bool IsRegistered<TService>(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(TService))
            {
                return true;
            }
        }

        return false;
    }
}
