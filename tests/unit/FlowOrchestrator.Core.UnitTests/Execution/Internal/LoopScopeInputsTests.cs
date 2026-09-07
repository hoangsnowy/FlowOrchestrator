using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Regression coverage for issue #176 — a <c>ForEach</c> child that declares a <c>RunAfter</c>
/// (so it is dispatched by the DAG continuation rather than fanned out by
/// <c>ForEachStepHandler</c>) never received <c>__loopItem</c> / <c>__loopIndex</c>.
/// </summary>
public class LoopScopeInputsTests
{
    // The shape reported in issue #176: an entry child (wait_robot_goto) plus a dependent
    // child (open_camera) that the loop handler therefore never dispatches itself.
    private static readonly StepCollection _steps = new()
    {
        ["scan_start"] = new StepMetadata { Type = "ScanStart" },
        ["scan_process"] = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.Steps",
            ConcurrencyLimit = 1,
            Steps = new StepCollection
            {
                ["wait_robot_goto"] = new StepMetadata { Type = "WaitForSignal" },
                ["open_camera"] = new StepMetadata
                {
                    Type = "OpenCamera",
                    RunAfter = new RunAfterCollection { { "wait_robot_goto", [StepStatus.Succeeded] } }
                }
            }
        }
    };

    private static JsonElement TriggerData => JsonSerializer.Deserialize<JsonElement>(
        "{\"OrderNo\":\"ORD-1\",\"Steps\":[{\"code\":\"A\"},{\"code\":\"B\"},{\"code\":\"C\"}]}");

    private static IDictionary<string, object?> Inputs(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dict[key] = value;
        }
        return dict;
    }

    private static IDictionary<string, object?> Apply(
        IDictionary<string, object?> inputs,
        string runtimeStepKey,
        StepCollection? steps = null,
        object? triggerData = null) =>
        LoopScopeInputs.Apply(inputs, runtimeStepKey, steps ?? _steps, triggerData ?? TriggerData, null);

    [Fact]
    public void DependentLoopChild_ReceivesLoopItemAndIndex()
    {
        // Arrange — exactly what the continuation builds: a copy of the loop's TEMPLATE inputs.
        var inputs = Inputs(("OrderNo", "ORD-1"));

        // Act
        var result = Apply(inputs, "scan_process.1.open_camera");

        // Assert
        Assert.Equal(1, result["__loopIndex"]);
        var item = Assert.IsType<Dictionary<string, object?>>(result["__loopItem"]);
        Assert.Equal("B", item["code"]);
        Assert.Equal("ORD-1", result["OrderNo"]);
    }

    [Fact]
    public void TopLevelStep_IsLeftUntouched()
    {
        // Arrange
        var inputs = Inputs(("OrderNo", "ORD-1"));

        // Act
        var result = Apply(inputs, "scan_start");

        // Assert — same instance back: the non-loop path must stay allocation-free.
        Assert.Same(inputs, result);
        Assert.False(result.ContainsKey("__loopItem"));
    }

    [Fact]
    public void FanOutInputs_KeepTheItemTheHandlerComputed()
    {
        // Arrange — an entry child already carries both keys from ForEachStepHandler.
        var handlerItem = new Dictionary<string, object?> { ["code"] = "HANDLER" };
        var inputs = Inputs(("__loopItem", handlerItem), ("__loopIndex", 0));

        // Act
        var result = Apply(inputs, "scan_process.0.wait_robot_goto");

        // Assert
        Assert.Same(inputs, result);
        Assert.Same(handlerItem, result["__loopItem"]);
    }

    [Fact]
    public void PartiallyPopulatedInputs_AreRecomputedFromTheRuntimeKey()
    {
        // Arrange — a runtime job store that dropped one of the two keys in transit.
        var inputs = Inputs(("__loopIndex", 2));

        // Act
        var result = Apply(inputs, "scan_process.2.open_camera");

        // Assert
        Assert.Equal(2, result["__loopIndex"]);
        var item = Assert.IsType<Dictionary<string, object?>>(result["__loopItem"]);
        Assert.Equal("C", item["code"]);
    }

    [Fact]
    public void IndexBeyondTheSource_KeepsTheIndexAndNullsTheItem()
    {
        // Arrange
        var inputs = Inputs();

        // Act
        var result = Apply(inputs, "scan_process.9.open_camera");

        // Assert
        Assert.Equal(9, result["__loopIndex"]);
        Assert.Null(result["__loopItem"]);
    }

    [Fact]
    public void KeyWithADottedSegmentThatIsNotALoop_IsLeftUntouched()
    {
        // Arrange — "scan_start" has no loop ancestor, so a numeric-looking path must not match.
        var steps = new StepCollection { ["scan_start"] = new StepMetadata { Type = "ScanStart" } };
        var inputs = Inputs(("OrderNo", "ORD-1"));

        // Act
        var result = Apply(inputs, "scan_start.0.child", steps);

        // Assert
        Assert.Same(inputs, result);
    }

    [Fact]
    public void NestedLoops_ResolveTheInnermostScope()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["outer"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.outer",
                Steps = new StepCollection
                {
                    ["inner"] = new LoopStepMetadata
                    {
                        Type = "ForEach",
                        ForEach = "@triggerBody()?.inner",
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
                }
            }
        };
        var trigger = JsonSerializer.Deserialize<JsonElement>(
            "{\"outer\":[\"o0\",\"o1\"],\"inner\":[\"i0\",\"i1\",\"i2\"]}");

        // Act
        var result = Apply(Inputs(), "outer.1.inner.2.follow", steps, trigger);

        // Assert — the innermost loop wins, matching what ForEachStepHandler injects at fan-out.
        Assert.Equal(2, result["__loopIndex"]);
        Assert.Equal("i2", result["__loopItem"]);
    }

    [Fact]
    public void LiteralManifestSource_IsIndexedWithoutTriggerData()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = new List<object?> { "red", "green", "blue" },
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

        // Act
        var result = LoopScopeInputs.Apply(Inputs(), "loop.2.follow", steps, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Equal(2, result["__loopIndex"]);
        Assert.Equal("blue", result["__loopItem"]);
    }

    [Fact]
    public void LoopItemThatLooksLikeAnExpression_IsNotEvaluated()
    {
        // Arrange — the injection runs after expression resolution precisely so an item whose text
        // starts with '@' survives verbatim instead of being mistaken for @triggerBody().
        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.items",
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
        var trigger = JsonSerializer.Deserialize<JsonElement>("{\"items\":[\"@triggerBody()?.OrderNo\"]}");

        // Act
        var result = Apply(Inputs(), "loop.0.follow", steps, trigger);

        // Assert
        Assert.Equal("@triggerBody()?.OrderNo", result["__loopItem"]);
    }
}
