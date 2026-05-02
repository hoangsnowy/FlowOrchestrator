using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Verifies that <see cref="ServiceBusFlowProcessorHostedService"/> consults
/// <see cref="IFlowStore"/> at startup and creates processors only for enabled flows.
/// </summary>
/// <remarks>
/// We can't fully boot the hosted service without a live SB endpoint, but we can drive the
/// branch where <c>StartAsync</c> would call <c>EnsureSubscriptionAsync</c> and observe the
/// admin client. A disabled flow must NOT cause an admin call; an enabled flow must.
/// </remarks>
public class ServiceBusFlowProcessorSkipDisabledTests
{
    [Fact]
    public async Task StartAsync_DisabledFlow_DoesNotCallEnsureSubscription()
    {
        // Arrange
        var (flowEnabled, flowDisabled, repo, store) = TwoFlowsOneDisabled();
        var topology = new TopologyManagerSpy();

        var sut = new ServiceBusFlowProcessorHostedService(
            client: new ServiceBusClient(FakeConnString),
            options: new ServiceBusRuntimeOptions
            {
                ConnectionString = FakeConnString,
                AutoCreateTopology = true,
            },
            topology: topology,
            repository: repo,
            flowStore: store,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            logger: NullLogger<ServiceBusFlowProcessorHostedService>.Instance);

        // Act — StartAsync will fail when it actually tries CreateProcessor on the fake conn,
        // but we only need to observe topology calls before that point.
        try { await sut.StartAsync(CancellationToken.None); } catch { /* expected — fake conn */ }

        // Assert
        Assert.Contains(flowEnabled.Id, topology.EnsuredSubscriptions);
        Assert.DoesNotContain(flowDisabled.Id, topology.EnsuredSubscriptions);
    }

    private const string FakeConnString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    private static (IFlowDefinition enabled, IFlowDefinition disabled, IFlowRepository repo, IFlowStore store) TwoFlowsOneDisabled()
    {
        var enabled = Substitute.For<IFlowDefinition>();
        enabled.Id.Returns(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var disabled = Substitute.For<IFlowDefinition>();
        disabled.Id.Returns(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        // Hand-rolled stubs avoid NSubstitute's ValueTask/Task interleaving quirk.
        var repo = new StubFlowRepository(enabled, disabled);
        var store = new StubFlowStore(
            new FlowDefinitionRecord { Id = enabled.Id, IsEnabled = true },
            new FlowDefinitionRecord { Id = disabled.Id, IsEnabled = false });

        return (enabled, disabled, repo, store);
    }

    private sealed class StubFlowRepository : IFlowRepository
    {
        private readonly IReadOnlyList<IFlowDefinition> _flows;
        public StubFlowRepository(params IFlowDefinition[] flows) => _flows = flows;
        public ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync() => new(_flows);
    }

    private sealed class StubFlowStore : IFlowStore
    {
        private readonly IReadOnlyList<FlowDefinitionRecord> _records;
        public StubFlowStore(params FlowDefinitionRecord[] records) => _records = records;
        public Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync() => Task.FromResult(_records);
        public Task<FlowDefinitionRecord?> GetByIdAsync(Guid id) =>
            Task.FromResult(_records.FirstOrDefault(r => r.Id == id));
        public Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record) => Task.FromResult(record);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled) =>
            Task.FromResult(_records.First(r => r.Id == id));
    }

    /// <summary>
    /// Captures which subscription IDs the hosted service requested. Inheriting from
    /// <see cref="ServiceBusTopologyManager"/> gives us a real reference type while
    /// overriding the methods that touch the admin client.
    /// </summary>
    private sealed class TopologyManagerSpy : ServiceBusTopologyManager
    {
        public List<Guid> EnsuredSubscriptions { get; } = new();

        public TopologyManagerSpy()
            : base(new ServiceBusAdministrationClient(FakeConnString),
                   new ServiceBusRuntimeOptions { ConnectionString = FakeConnString },
                   NullLogger<ServiceBusTopologyManager>.Instance)
        { }

        public override Task EnsureTopicAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task EnsureCronQueueAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task EnsureSubscriptionAsync(Guid flowId, CancellationToken ct = default)
        {
            EnsuredSubscriptions.Add(flowId);
            return Task.CompletedTask;
        }
    }
}
