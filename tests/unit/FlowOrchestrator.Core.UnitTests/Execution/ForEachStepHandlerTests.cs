using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

public class ForEachStepHandlerTests
{
    private readonly ForEachStepHandler _sut = new();

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IExecutionContext MakeContext(object? triggerData = null)
    {
        var ctx = Substitute.For<IExecutionContext>();
        ctx.RunId.Returns(Guid.NewGuid());
        ctx.TriggerData.Returns(triggerData);
        ctx.TriggerHeaders.Returns((IReadOnlyDictionary<string, string>?)null);
        return ctx;
    }

    private static IFlowDefinition FlowWith(string stepKey, StepMetadata meta)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection { [stepKey] = meta }
        });
        return flow;
    }

    private static IStepInstance MakeStep(string key = "loop1")
    {
        var step = Substitute.For<IStepInstance>();
        step.Key.Returns(key);
        return step;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonLoopStep_ReturnsNull()
    {
        // Arrange
        var flow = FlowWith("loop1", new StepMetadata { Type = "Other" });

        // Act
        var result = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyItems_ReturnsZeroIterations()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = new List<object>(),
            Steps = new StepCollection { ["child"] = new StepMetadata { Type = "DoWork" } }
        };
        var flow = FlowWith("loop1", loop);

        // Act
        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep());

        // Assert
        var sr = Assert.IsType<StepResult>(raw);
        Assert.Equal(StepStatus.Succeeded, sr.Status);
        Assert.Null(sr.DispatchHint);
        Assert.Equal(0, JsonSerializer.SerializeToElement(sr.Result).GetProperty("iterations").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_StaticList_ReturnsDispatchHintWithChildren()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = new List<object?> { "a", "b", "c" },
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata { Type = "DoWork", RunAfter = new(), Inputs = new Dictionary<string, object?>() }
            }
        };
        var flow = FlowWith("loop1", loop);

        // Act
        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));

        // Assert
        var sr = Assert.IsType<StepResult>(raw);
        Assert.Equal(StepStatus.Succeeded, sr.Status);
        Assert.NotNull(sr.DispatchHint);
        var spawn = sr.DispatchHint!.Spawn;
        Assert.Equal(3, spawn.Count);
        Assert.Equal("loop1.0.child", spawn[0].StepKey);
        Assert.Equal("loop1.1.child", spawn[1].StepKey);
        Assert.Equal("loop1.2.child", spawn[2].StepKey);
        Assert.All(spawn, r => Assert.Equal("DoWork", r.StepType));
    }

    [Fact]
    public async Task ExecuteAsync_ChildInputsContainLoopItemAndIndex()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = new List<object?> { "x" },
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata
                {
                    Type = "DoWork",
                    RunAfter = new(),
                    Inputs = new Dictionary<string, object?> { ["staticKey"] = "staticVal" }
                }
            }
        };
        var flow = FlowWith("loop1", loop);

        // Act
        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));

        // Assert
        var spawn = ((StepResult)raw!).DispatchHint!.Spawn;
        var inputs = spawn[0].Inputs;
        Assert.Equal("x", inputs["__loopItem"]);
        Assert.Equal(0, inputs["__loopIndex"]);
        Assert.Equal("staticVal", inputs["staticKey"]);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_AddsDelayForLaterBuckets()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ConcurrencyLimit = 2,
            ForEach = new List<object?> { "a", "b", "c", "d" },
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata { Type = "DoWork", RunAfter = new(), Inputs = new Dictionary<string, object?>() }
            }
        };
        var flow = FlowWith("loop1", loop);

        // Act
        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));
        var spawn = ((StepResult)raw!).DispatchHint!.Spawn;

        // Assert
        Assert.Null(spawn[0].Delay);
        Assert.Null(spawn[1].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), spawn[2].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), spawn[3].Delay);
    }

    [Fact]
    public async Task ExecuteAsync_JsonArrayTriggerBody_ResolvesItems()
    {
        // Arrange
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = "@triggerBody()?.items",
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata { Type = "DoWork", RunAfter = new(), Inputs = new Dictionary<string, object?>() }
            }
        };
        var flow = FlowWith("loop1", loop);
        var triggerData = JsonSerializer.Deserialize<JsonElement>("{\"items\":[1,2,3]}");

        // Act
        var raw = await _sut.ExecuteAsync(MakeContext(triggerData), flow, MakeStep("loop1"));

        // Assert
        var sr = (StepResult)raw!;
        Assert.Equal(3, sr.DispatchHint!.Spawn.Count);
    }
}
