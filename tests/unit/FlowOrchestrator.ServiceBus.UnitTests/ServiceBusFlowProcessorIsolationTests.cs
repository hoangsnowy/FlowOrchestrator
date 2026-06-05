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
/// Regression coverage for per-flow startup isolation in
/// <see cref="ServiceBusFlowProcessorHostedService"/>: a topology / start-processing failure for ONE
/// flow must not abort host startup and silently kill processing for the OTHER flows. Before the fix,
/// the <c>foreach</c> in <c>StartAsync</c> had no per-flow try/catch, so the first flow that threw on
/// <c>EnsureSubscriptionAsync</c> (or <c>StartProcessingAsync</c>) propagated out of <c>StartAsync</c>
/// and prevented every subsequent flow from getting a processor.
/// </summary>
public class ServiceBusFlowProcessorIsolationTests
{
    private const string FakeConnString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    [Fact]
    public async Task StartAsync_FirstFlowTopologyThrows_StillEnsuresSecondFlow()
    {
        // Arrange — two enabled flows; the FIRST one throws on EnsureSubscriptionAsync.
        var first = StubFlow("11111111-1111-1111-1111-111111111111");
        var second = StubFlow("22222222-2222-2222-2222-222222222222");
        var repo = new StubFlowRepository(first, second);
        var store = new StubFlowStore(
            new FlowDefinitionRecord { Id = first.Id, IsEnabled = true },
            new FlowDefinitionRecord { Id = second.Id, IsEnabled = true });
        var topology = new ThrowingTopologyManagerSpy(throwForFlowId: first.Id);

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

        // Act — StartAsync must NOT bubble the first flow's failure; it logs and continues.
        // (StartProcessingAsync on a fake connection never actually connects, but the assertion
        // below only needs to observe that the SECOND flow's topology step was reached.)
        var ex = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));

        // Assert — the second flow's EnsureSubscriptionAsync was reached despite the first throwing.
        Assert.Null(ex);
        Assert.Contains(first.Id, topology.AttemptedSubscriptions);
        Assert.Contains(second.Id, topology.AttemptedSubscriptions);
    }

    [Fact]
    public async Task StartAsync_FirstFlowThrows_DoesNotPropagate()
    {
        // Arrange — single flow that throws; the failure must be swallowed (logged), not rethrown.
        var only = StubFlow("33333333-3333-3333-3333-333333333333");
        var repo = new StubFlowRepository(only);
        var store = new StubFlowStore(new FlowDefinitionRecord { Id = only.Id, IsEnabled = true });
        var topology = new ThrowingTopologyManagerSpy(throwForFlowId: only.Id);

        var sut = new ServiceBusFlowProcessorHostedService(
            client: new ServiceBusClient(FakeConnString),
            options: new ServiceBusRuntimeOptions { ConnectionString = FakeConnString, AutoCreateTopology = true },
            topology: topology,
            repository: repo,
            flowStore: store,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            logger: NullLogger<ServiceBusFlowProcessorHostedService>.Instance);

        // Act
        var ex = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));

        // Assert — host startup is not aborted by a single flow's failure.
        Assert.Null(ex);
        Assert.Contains(only.Id, topology.AttemptedSubscriptions);
    }

    private static IFlowDefinition StubFlow(string id)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.Parse(id));
        return flow;
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
    /// Topology spy that records every flow whose subscription was attempted and throws for a
    /// configured flow id, simulating a transient management-plane failure. Recording happens
    /// BEFORE the throw so the test can prove the loop reached each flow.
    /// </summary>
    private sealed class ThrowingTopologyManagerSpy : ServiceBusTopologyManager
    {
        private readonly Guid _throwForFlowId;

        public List<Guid> AttemptedSubscriptions { get; } = new();

        public ThrowingTopologyManagerSpy(Guid throwForFlowId)
            : base(new ServiceBusAdministrationClient(FakeConnString),
                   new ServiceBusRuntimeOptions { ConnectionString = FakeConnString },
                   NullLogger<ServiceBusTopologyManager>.Instance)
        {
            _throwForFlowId = throwForFlowId;
        }

        public override Task EnsureTopicAsync(CancellationToken ct = default) => Task.CompletedTask;
        public override Task EnsureCronQueueAsync(CancellationToken ct = default) => Task.CompletedTask;

        public override Task EnsureSubscriptionAsync(Guid flowId, CancellationToken ct = default)
        {
            AttemptedSubscriptions.Add(flowId);
            if (flowId == _throwForFlowId)
            {
                throw new InvalidOperationException("Simulated topology failure for this flow.");
            }
            return Task.CompletedTask;
        }
    }
}
