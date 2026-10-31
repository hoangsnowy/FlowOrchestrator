using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// DI registration helpers for the Azure Service Bus runtime adapter.
/// </summary>
public static class FlowOrchestratorBuilderServiceBusExtensions
{
    /// <summary>
    /// Registers the Azure Service Bus step-execution runtime: a topic-based
    /// <see cref="IStepDispatcher"/>, per-flow subscription processors, and a
    /// self-perpetuating cron message scheduler.
    /// </summary>
    /// <param name="builder">The FlowOrchestrator builder to register into.</param>
    /// <param name="configure">Configuration callback that fills in connection string + naming.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// Call this inside <c>AddFlowOrchestrator(opts =&gt; opts.UseAzureServiceBusRuntime(...))</c>
    /// instead of <c>UseHangfire()</c> or <c>UseInMemoryRuntime()</c>. The Service Bus runtime is
    /// registered with <c>TryAdd</c> semantics, so it can co-exist with — and override — the
    /// default Hangfire dispatcher registered by <c>AddFlowOrchestrator</c>.
    /// </remarks>
    public static FlowOrchestratorBuilder UseAzureServiceBusRuntime(
        this FlowOrchestratorBuilder builder,
        Action<ServiceBusRuntimeOptions> configure)
    {
        var options = new ServiceBusRuntimeOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "ServiceBusRuntimeOptions.ConnectionString must be configured for UseAzureServiceBusRuntime().");
        }

        builder.Services.AddSingleton(options);

        builder.Services.TryAddSingleton(sp => new ServiceBusClient(options.ConnectionString));
        builder.Services.TryAddSingleton(sp => new ServiceBusAdministrationClient(options.ConnectionString));
        builder.Services.TryAddSingleton<ServiceBusTopologyManager>();

        builder.Services.TryAddSingleton<IStepDispatcher, ServiceBusStepDispatcher>();

        builder.Services.AddScoped<IServiceBusFlowRunner, ServiceBusFlowOrchestrator>();

        builder.Services.TryAddSingleton<ServiceBusRecurringTriggerHub>();
        builder.Services.TryAddSingleton<IRecurringTriggerDispatcher>(
            sp => sp.GetRequiredService<ServiceBusRecurringTriggerHub>());
        builder.Services.TryAddSingleton<IRecurringTriggerInspector>(
            sp => sp.GetRequiredService<ServiceBusRecurringTriggerHub>());
        builder.Services.TryAddSingleton<IRecurringTriggerSync>(
            sp => sp.GetRequiredService<ServiceBusRecurringTriggerHub>());

        builder.Services.AddHostedService<ServiceBusFlowProcessorHostedService>();
        builder.Services.AddHostedService<ServiceBusCronProcessorHostedService>();

        return builder;
    }
}
