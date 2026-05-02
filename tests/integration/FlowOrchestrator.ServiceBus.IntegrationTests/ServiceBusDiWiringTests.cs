using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using global::Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// End-to-end DI wiring smoke tests for the Service Bus runtime adapter. These verify that
/// <c>UseAzureServiceBusRuntime</c> overrides the Hangfire defaults registered inside
/// <c>AddFlowOrchestrator</c> and that every recurring-trigger interface resolves to the
/// same hub instance (parity with the InMemory dispatcher).
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

        // Hangfire infrastructure has to be registered for AddFlowOrchestrator to wire its
        // engine plumbing; our SB runtime then overrides the dispatcher trio via TryAdd.
        services.AddHangfire(c => c.UseInMemoryStorage());

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

        // Assert — same singleton serves all three contracts (parity with InMemory).
        Assert.IsType<ServiceBusRecurringTriggerHub>(dispatcher);
        Assert.Same(dispatcher, inspector);
        Assert.Same(dispatcher, sync);
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
        services.AddHangfire(c => c.UseInMemoryStorage());

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            services.AddFlowOrchestrator(opts =>
            {
                opts.UseInMemory();
                opts.UseAzureServiceBusRuntime(sb => sb.ConnectionString = "");
            }));
    }
}
