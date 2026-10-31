using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// #192: step dispatch sends over its own <see cref="ServiceBusClient"/> (a dedicated AMQP connection) by
/// default, instead of queueing behind every flow processor's receive links on the shared connection.
/// </summary>
public sealed class ServiceBusDedicatedSenderConnectionTests
{
    // Parsing only — ServiceBusClient connects lazily, so no broker is needed.
    private const string ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private static ServiceProvider Build(bool? dedicated)
    {
        var services = new ServiceCollection();
        new FlowOrchestratorBuilder(services).UseAzureServiceBusRuntime(o =>
        {
            o.ConnectionString = ConnectionString;
            if (dedicated.HasValue) o.UseDedicatedSenderConnection = dedicated.Value;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Dispatcher_uses_its_own_client_by_default()
    {
        // Arrange
        await using var provider = Build(dedicated: null);

        // Act
        var dispatcher = (ServiceBusStepDispatcher)provider.GetRequiredService<IStepDispatcher>();
        var shared = provider.GetRequiredService<ServiceBusClient>();

        // Assert
        Assert.NotSame(shared, dispatcher.Client);
    }

    [Fact]
    public async Task Dispatcher_shares_the_client_when_opted_out()
    {
        // Arrange
        await using var provider = Build(dedicated: false);

        // Act
        var dispatcher = (ServiceBusStepDispatcher)provider.GetRequiredService<IStepDispatcher>();
        var shared = provider.GetRequiredService<ServiceBusClient>();

        // Assert
        Assert.Same(shared, dispatcher.Client);
    }

    [Fact]
    public async Task Dedicated_client_is_disposed_with_the_dispatcher_and_shared_one_is_not()
    {
        // Arrange
        var dedicated = new ServiceBusClient(ConnectionString);
        var shared = new ServiceBusClient(ConnectionString);
        var owning = new ServiceBusStepDispatcher(dedicated, new ServiceBusRuntimeOptions(), ownsClient: true);
        var borrowing = new ServiceBusStepDispatcher(shared, new ServiceBusRuntimeOptions());

        // Act
        await owning.DisposeAsync();
        await borrowing.DisposeAsync();

        // Assert
        Assert.True(dedicated.IsClosed);
        Assert.False(shared.IsClosed);
        await shared.DisposeAsync();
    }
}
