using FlowOrchestrator.Core.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Abstractions;

/// <summary>
/// Contract of <see cref="FlowRegistry"/>, the single code-defined flow registry that replaced the
/// 1.x <c>IFlowRepository</c> and its per-backend copies.
/// </summary>
public sealed class FlowRegistryTests
{
    private static IFlowDefinition Flow(Guid id)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(id);
        return flow;
    }

    [Fact]
    public void Flows_ListsEveryRegistration_InRegistrationOrder()
    {
        // Arrange
        var first = Flow(Guid.NewGuid());
        var second = Flow(Guid.NewGuid());

        // Act
        var registry = new FlowRegistry([first, second]);

        // Assert
        Assert.Equal([first, second], registry.Flows);
    }

    [Fact]
    public void TryGetFlow_RegisteredId_ReturnsTheDefinition()
    {
        // Arrange
        var id = Guid.NewGuid();
        var flow = Flow(id);
        var registry = new FlowRegistry([Flow(Guid.NewGuid()), flow]);

        // Act
        var found = registry.TryGetFlow(id, out var resolved);

        // Assert
        Assert.True(found);
        Assert.Same(flow, resolved);
        Assert.Same(flow, registry.Find(id));
    }

    [Fact]
    public void TryGetFlow_UnknownId_ReturnsFalse()
    {
        // Arrange
        var registry = new FlowRegistry([Flow(Guid.NewGuid())]);

        // Act
        var found = registry.TryGetFlow(Guid.NewGuid(), out var resolved);

        // Assert
        Assert.False(found);
        Assert.Null(resolved);
        Assert.Null(registry.Find(Guid.NewGuid()));
    }

    [Fact]
    public void SameFlowTypeRegisteredTwice_KeepsTheFirstRegistration()
    {
        // Arrange — AddFlow<TFlow>() called twice yields two instances of one type.
        var id = Guid.NewGuid();
        var first = new FlowA(id);
        var repeat = new FlowA(id);

        // Act
        var registry = new FlowRegistry([first, repeat]);

        // Assert
        Assert.Same(first, registry.Find(id));
        Assert.Same(first, Assert.Single(registry.Flows));
    }

    [Fact]
    public void DifferentFlowTypesSharingAnId_FailFast_NamingBothTypesAndTheId()
    {
        // Arrange
        var id = Guid.NewGuid();
        IFlowDefinition[] flows = [new FlowA(id), new FlowB(id)];

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => new FlowRegistry(flows));

        // Assert
        Assert.Contains(typeof(FlowA).FullName!, ex.Message);
        Assert.Contains(typeof(FlowB).FullName!, ex.Message);
        Assert.Contains(id.ToString(), ex.Message);
    }

    private sealed class FlowA(Guid id) : IFlowDefinition
    {
        public Guid Id { get; } = id;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }

    private sealed class FlowB(Guid id) : IFlowDefinition
    {
        public Guid Id { get; } = id;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }

    [Fact]
    public void Empty_HasNoFlows()
    {
        // Arrange
        var registry = FlowRegistry.Empty;

        // Act
        var found = registry.TryGetFlow(Guid.Empty, out _);

        // Assert
        Assert.Empty(registry.Flows);
        Assert.False(found);
    }

    [Fact]
    public void Constructor_NullSequence_Throws()
    {
        // Arrange
        IEnumerable<IFlowDefinition> flows = null!;

        // Act
        var act = () => new FlowRegistry(flows);

        // Assert
        Assert.Throws<ArgumentNullException>(act);
    }
}
