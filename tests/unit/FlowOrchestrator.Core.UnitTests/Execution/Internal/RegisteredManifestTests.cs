using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using static FlowOrchestrator.Core.Tests.Execution.ManifestDeployHarness;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the comparison a worker uses to decide whether it holds the flow's registered manifest — the
/// question behind handing back a step its manifest does not contain during a rolling deploy.
/// </summary>
public sealed class RegisteredManifestTests
{
    private static readonly Guid FlowId = Guid.NewGuid();

    private static IFlowDefinition TwoSteps() => Flow(FlowId, "1", new StepCollection { ["a"] = Work(), ["b"] = Work("a") });

    [Fact]
    public void Matches_StoredFormOfSameManifest_ReturnsTrue()
    {
        // Arrange
        var flow = TwoSteps();
        var stored = RegisteredManifest.Serialize(TwoSteps());

        // Act
        var matches = RegisteredManifest.Matches(stored, flow);

        // Assert
        Assert.True(matches);
    }

    [Fact]
    public void Matches_StoreNormalisedWhitespaceAndPropertyOrder_ReturnsTrue()
    {
        // Arrange
        var flow = TwoSteps();
        using var doc = System.Text.Json.JsonDocument.Parse(RegisteredManifest.Serialize(flow));
        var reordered = "{ \"steps\" : " + doc.RootElement.GetProperty("steps").GetRawText()
            + " ,\n \"triggers\" : " + doc.RootElement.GetProperty("triggers").GetRawText() + " }";

        // Act
        var matches = RegisteredManifest.Matches(reordered, flow);

        // Assert
        Assert.True(matches);
    }

    [Fact]
    public void Matches_ManifestWithAnotherStep_ReturnsFalse()
    {
        // Arrange
        var flow = TwoSteps();
        var newer = RegisteredManifest.Serialize(Flow(FlowId, "2", new StepCollection { ["a"] = Work(), ["b"] = Work("a"), ["c"] = Work("b") }));

        // Act
        var matches = RegisteredManifest.Matches(newer, flow);

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void Matches_ChangedRunAfterOnly_ReturnsFalse()
    {
        // Arrange
        var flow = TwoSteps();
        var rewired = RegisteredManifest.Serialize(Flow(FlowId, "2", new StepCollection { ["a"] = Work(), ["b"] = Work() }));

        // Act
        var matches = RegisteredManifest.Matches(rewired, flow);

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void Matches_StoredValueNotJson_ReturnsFalse()
    {
        // Arrange
        var flow = TwoSteps();

        // Act
        var matches = RegisteredManifest.Matches("{ not json", flow);

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void Matches_ChildAddedInsideExistingScopeOnly_ReturnsFalse()
    {
        // Arrange — the persisted manifest carries a scope's nested steps, so a change confined to a ForEach
        // body still tells a stale worker that another version is live.
        static IFlowDefinition LoopFlow(string version, params string[] children)
        {
            var body = new StepCollection();
            foreach (var child in children)
            {
                body[child] = Work();
            }

            return Flow(FlowId, version, new StepCollection
            {
                ["loop"] = new LoopStepMetadata { Type = "ForEach", ForEach = new[] { 1, 2 }, Steps = body }
            });
        }

        var stale = LoopFlow("1", "a");
        var newer = RegisteredManifest.Serialize(LoopFlow("2", "a", "b"));

        // Act
        var matches = RegisteredManifest.Matches(newer, stale);

        // Assert
        Assert.False(matches);
    }

    [Fact]
    public void Serialize_SameDefinition_IsCachedPerInstance()
    {
        // Arrange
        var flow = TwoSteps();

        // Act
        var first = RegisteredManifest.Serialize(flow);
        var second = RegisteredManifest.Serialize(flow);

        // Assert
        Assert.Same(first, second);
    }
}
