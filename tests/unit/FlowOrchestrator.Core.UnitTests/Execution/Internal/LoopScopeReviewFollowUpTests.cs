using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Coverage for the gaps a review of the #176 / #177 fix found: the all-or-nothing input guard,
/// the divergent <c>__loopItem</c> semantics between the fan-out and recompute paths, the
/// discarded dictionary comparer, the narrow <see cref="ForEachSourceResolver.TryGetItemAt"/>
/// fast path, and the untested coupling between the two fixes.
/// </summary>
public class LoopScopeReviewFollowUpTests
{
    private static StepCollection LoopWith(object? forEachSource) => new()
    {
        ["loop"] = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = forEachSource,
            Steps = new StepCollection
            {
                ["lead"] = new StepMetadata { Type = "Lead" },
                ["follow"] = new StepMetadata
                {
                    Type = "Follow",
                    RunAfter = new RunAfterCollection { { "lead", [StepStatus.Succeeded] } }
                }
            }
        }
    };

    private static IDictionary<string, object?> Apply(
        IDictionary<string, object?> inputs,
        string key,
        StepCollection steps,
        object? triggerData)
    {
        Assert.True(LoopScopeInputs.TryResolveScope(key, steps, out var scope));
        return LoopScopeInputs.Apply(inputs, scope, triggerData, null);
    }

    [Fact]
    public void ItemPresentButIndexMissing_KeepsTheItemInsteadOfNullingIt()
    {
        // Arrange — a dispatcher dropped the int __loopIndex but kept the item, and the source can
        // no longer be materialised (no trigger data on this path).
        var steps = LoopWith("@triggerBody()?.items");
        var item = new Dictionary<string, object?> { ["code"] = "KEEP" };
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["__loopItem"] = item };

        // Act
        var result = Apply(inputs, "loop.1.follow", steps, triggerData: null);

        // Assert — the index is filled in, the surviving item is NOT overwritten with null.
        Assert.Equal(1, result["__loopIndex"]);
        Assert.Same(item, result["__loopItem"]);
    }

    [Fact]
    public void NullItemWithIndexPresent_IsStillRecomputed()
    {
        // Arrange — a genuinely absent item must still be filled in.
        var steps = LoopWith("@triggerBody()?.items");
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"items\":[\"a\",\"b\"]}");
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__loopIndex"] = 1,
            ["__loopItem"] = null
        };

        // Act
        var result = Apply(inputs, "loop.1.follow", steps, trigger);

        // Assert
        Assert.Equal("b", result["__loopItem"]);
    }

    [Fact]
    public void CaseInsensitiveInputComparer_SurvivesTheLoopScopeCopy()
    {
        // Arrange — a dispatcher that hands the engine case-insensitive inputs.
        var steps = LoopWith("@triggerBody()?.items");
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"items\":[\"a\"]}");
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["OrderNo"] = "ORD-1" };

        // Act
        var result = Apply(inputs, "loop.0.follow", steps, trigger);

        // Assert — the copy must not silently downgrade to ordinal on loop children only.
        Assert.Equal("ORD-1", result["orderno"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ListOfString_IsIndexedDirectly(int index)
    {
        // Arrange — List<string> is NOT IList<object?> (generic invariance), so an IList<object?>
        // fast path would silently fall back to materialising every item.
        var source = new List<string> { "red", "green", "blue" };

        // Act
        var found = ForEachSourceResolver.TryGetItemAt(source, index, out var item);

        // Assert
        Assert.True(found);
        Assert.Equal(source[index], item);
    }

    [Fact]
    public void StringArraySource_IsIndexedDirectly()
    {
        // Arrange
        var source = new[] { "x", "y" };

        // Act
        var found = ForEachSourceResolver.TryGetItemAt(source, 1, out var item);

        // Assert
        Assert.True(found);
        Assert.Equal("y", item);
    }

    [Fact]
    public void StringSource_IsNotTreatedAsAnIndexableCollection()
    {
        // Arrange — a bare string must not iterate as characters.

        // Act
        var found = ForEachSourceResolver.TryGetItemAt("abc", 1, out var item);

        // Assert
        Assert.False(found);
        Assert.Null(item);
    }

    [Fact]
    public async Task PascalCaseForEachSource_SurvivesTheTriggerDataStoreRoundTrip()
    {
        // Arrange — this is the coupling the whole #176 fix rests on: the recompute re-resolves the
        // ForEach source from trigger data that has been round-tripped through an outputs
        // repository, which persists with web (camelCase) conventions. A PascalCase manifest path
        // only survives that because of the #177 relaxed property lookup. Narrowing that fallback
        // would make __loopItem silently null on every non-entry loop child.
        var repo = new InMemoryOutputsRepository();
        var runId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var steps = LoopWith("@triggerBody()?.Steps");
        flow.Manifest.Returns(new FlowManifest { Steps = steps });

        var trigger = Substitute.For<ITrigger>();
        trigger.Data.Returns(new { Steps = new[] { new { Code = "A" }, new { Code = "B" } } });
        var triggerCtx = Substitute.For<ITriggerContext>();
        triggerCtx.RunId.Returns(runId);
        await repo.SaveTriggerDataAsync(triggerCtx, flow, trigger);

        var rehydrated = await repo.GetTriggerDataAsync(runId);

        // Act
        var result = Apply(new Dictionary<string, object?>(StringComparer.Ordinal), "loop.1.follow", steps, rehydrated);

        // Assert
        Assert.Equal(1, result["__loopIndex"]);
        var item = Assert.IsType<Dictionary<string, object?>>(result["__loopItem"]);
        Assert.Equal("B", item["code"]);
    }

    [Fact]
    public void FanOutAndRecompute_AgreeOnAnItemThatLooksLikeAnExpression()
    {
        // Arrange — the fan-out writes __loopItem BEFORE expression resolution, the recompute
        // fills it in after. Unless the resolver reserves the loop keys, an entry child and a
        // dependent child of the SAME iteration observe different items.
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"items\":[\"@triggerBody()?.OrderNo\"],\"OrderNo\":\"ORD-1\"}");
        var steps = LoopWith("@triggerBody()?.items");

        var fanOutInputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__loopItem"] = "@triggerBody()?.OrderNo",
            ["__loopIndex"] = 0
        };

        // Act — entry child: goes through the resolution pass carrying a baked-in item.
        var afterResolution = InputResolutionPipeline.Resolve(fanOutInputs, trigger, null);

        // Dependent child: template inputs, context filled in afterwards.
        var recomputed = Apply(new Dictionary<string, object?>(StringComparer.Ordinal), "loop.0.follow", steps, trigger);

        // Assert
        Assert.Equal("@triggerBody()?.OrderNo", afterResolution["__loopItem"]);
        Assert.Equal(afterResolution["__loopItem"], recomputed["__loopItem"]);
    }

    [Fact]
    public void FanOutAndRecompute_AgreeOnTheShapeOfACollectionItem()
    {
        // Arrange — the resolution pass converted a List<object?> item to object[] via ToArray()
        // while the recompute leaves a List; handlers binding the item saw different shapes.
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"items\":[[1,2]]}");
        var steps = LoopWith("@triggerBody()?.items");

        ForEachSourceResolver.TryGetItemAt(
            ForEachSourceResolver.Resolve("@triggerBody()?.items", trigger, null), 0, out var fanOutItem);
        var fanOutInputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__loopItem"] = fanOutItem,
            ["__loopIndex"] = 0
        };

        // Act
        var afterResolution = InputResolutionPipeline.Resolve(fanOutInputs, trigger, null);
        var recomputed = Apply(new Dictionary<string, object?>(StringComparer.Ordinal), "loop.0.follow", steps, trigger);

        // Assert
        Assert.Equal(
            afterResolution["__loopItem"]!.GetType(),
            recomputed["__loopItem"]!.GetType());
    }

    [Fact]
    public void ScopeResolution_UsesIScopedStepNotTheConcreteLoopType()
    {
        // Arrange — every other scope-aware site in the engine matches on IScopedStep so new scope
        // kinds inherit the behaviour; the index must be resolved for any scoped step.
        var steps = new StepCollection
        {
            ["scope"] = new CustomScopedStep
            {
                Type = "CustomScope",
                Steps = new StepCollection { ["child"] = new StepMetadata { Type = "Work" } }
            }
        };

        // Act
        var resolved = LoopScopeInputs.TryResolveScope("scope.2.child", steps, out var scope);

        // Assert
        Assert.True(resolved);
        Assert.Equal(2, scope.Index);
    }

    /// <summary>A second <see cref="IScopedStep"/> implementation, standing in for a future scope kind.</summary>
    private sealed class CustomScopedStep : StepMetadata, IScopedStep
    {
        public StepCollection Steps { get; set; } = [];
    }
}
