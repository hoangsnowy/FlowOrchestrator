using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// End-to-end DI wiring smoke tests for the Service Bus runtime adapter. These verify that
/// <c>UseAzureServiceBusRuntime</c> wires a complete runtime on its own — no Hangfire package or
/// registration — and that the recurring-trigger dispatcher and inspector resolve to the same hub
/// instance while cron sync is Core's runtime-agnostic <see cref="RecurringTriggerSync"/>.
/// </summary>
/// <remarks>
/// No live Service Bus connection is required: <c>ServiceBusClient</c> only opens the
/// connection on first send/receive, so DI resolution alone hits zero network calls.
/// </remarks>
public class ServiceBusDiWiringTests
{
    private const string FakeConnString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();


        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseAzureServiceBusRuntime(sb =>
            {
                sb.ConnectionString = FakeConnString;
                sb.AutoCreateTopology = false;
            });
        });
        return services;
    }

    [Fact]
    public void StepDispatcher_ResolvesToServiceBusImplementation()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var dispatcher = sp.GetRequiredService<IStepDispatcher>();

        // Assert
        Assert.IsType<ServiceBusStepDispatcher>(dispatcher);
    }

    [Fact]
    public void RecurringTriggerInterfaces_AllResolveToSameHub()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();
        var inspector = sp.GetRequiredService<IRecurringTriggerInspector>();
        var sync = sp.GetRequiredService<IRecurringTriggerSync>();

        // Assert — one hub serves dispatcher + inspector (parity with InMemory); the effective-cron
        // sync is Core's single implementation driving that hub.
        Assert.IsType<ServiceBusRecurringTriggerHub>(dispatcher);
        Assert.Same(dispatcher, inspector);
        Assert.IsType<RecurringTriggerSync>(sync);
    }

    [Fact]
    public void ServiceBusClient_IsSingletonAndShared()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var c1 = sp.GetRequiredService<ServiceBusClient>();
        var c2 = sp.GetRequiredService<ServiceBusClient>();

        // Assert
        Assert.Same(c1, c2);
    }

    [Fact]
    public void AdminClient_IsRegistered()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var admin = sp.GetRequiredService<ServiceBusAdministrationClient>();

        // Assert
        Assert.NotNull(admin);
    }

    [Fact]
    public void TopologyManager_IsRegistered()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var topology = sp.GetRequiredService<ServiceBusTopologyManager>();
        var subName = topology.SubscriptionName(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        // Assert
        Assert.Equal("flow-33333333-3333-3333-3333-333333333333", subName);
    }

    [Fact]
    public void Options_AreRegisteredWithSuppliedConnectionString()
    {
        // Arrange
        var sp = BuildServices().BuildServiceProvider();

        // Act
        var options = sp.GetRequiredService<ServiceBusRuntimeOptions>();

        // Assert
        Assert.Equal(FakeConnString, options.ConnectionString);
        Assert.False(options.AutoCreateTopology);
        Assert.Equal("flow-steps", options.StepTopicName);
        Assert.Equal("flow-cron-triggers", options.CronQueueName);
    }

    [Fact]
    public void MissingConnectionString_Throws()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddFlowOrchestrator(opts =>
            {
                opts.UseInMemory();
                opts.UseAzureServiceBusRuntime(sb => sb.ConnectionString = "");
            }));
    }
}
