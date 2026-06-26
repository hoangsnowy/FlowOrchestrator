using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Regression: ForEach must materialise iteration items into plain CLR values before they
/// become <c>__loopItem</c> on each child step. A raw <see cref="JsonElement"/> does NOT
/// round-trip through Newtonsoft.Json (the Hangfire / Service Bus runtime job stores), so a
/// JsonElement item silently failed the child job and hung the run on those runtimes.
/// </summary>
public sealed class ForEachItemMaterializationTests
{
    [Fact]
    public async Task ExecuteAsync_JsonArraySource_ChildLoopItemsAreClrStrings_NotJsonElement()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.orderIds",
            Steps = new StepCollection { ["validate"] = new StepMetadata { Type = "Work" } }
        };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection { ["loop"] = loop } });

        var ctx = new CoreExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = JsonDocument.Parse("""{"orderIds":["ORD-001","ORD-002","ORD-003"]}""").RootElement,
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        var step = new StepInstance("loop", "ForEach") { RunId = ctx.RunId };

        // Act
        var result = await new ForEachStepHandler().ExecuteAsync(ctx, flow, step);

        // Assert
        var stepResult = Assert.IsType<StepResult>(result);
        Assert.Equal(StepStatus.Succeeded, stepResult.Status);
        Assert.NotNull(stepResult.DispatchHint);
        var loopItems = stepResult.DispatchHint!.Spawn.Select(child => child.Inputs["__loopItem"]).ToList();
        Assert.Equal(3, loopItems.Count);
        Assert.All(loopItems, item => Assert.IsType<string>(item));
        Assert.Equal(new[] { "ORD-001", "ORD-002", "ORD-003" }, loopItems.Cast<string>());
    }

    [Fact]
    public async Task ExecuteAsync_JsonObjectItems_MaterialiseToDictionaries_NotJsonElement()
    {
        // Arrange — object-shaped items must also materialise to a Newtonsoft-friendly graph.
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.orders",
            Steps = new StepCollection { ["validate"] = new StepMetadata { Type = "Work" } }
        };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection { ["loop"] = loop } });

        var ctx = new CoreExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerData = JsonDocument.Parse("""{"orders":[{"id":1},{"id":2}]}""").RootElement,
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        var step = new StepInstance("loop", "ForEach") { RunId = ctx.RunId };

        // Act
        var result = await new ForEachStepHandler().ExecuteAsync(ctx, flow, step);

        // Assert
        var stepResult = Assert.IsType<StepResult>(result);
        var loopItems = stepResult.DispatchHint!.Spawn.Select(child => child.Inputs["__loopItem"]).ToList();
        Assert.Equal(2, loopItems.Count);
        Assert.All(loopItems, item => Assert.IsType<Dictionary<string, object?>>(item));
        Assert.Equal(1L, ((Dictionary<string, object?>)loopItems[0]!)["id"]);
    }
}
