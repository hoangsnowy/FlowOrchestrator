using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;
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
        var flow = FlowWith("loop1", new StepMetadata { Type = "Other" });
        var result = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyItems_ReturnsZeroIterations()
    {
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ForEach = new List<object>(),
            Steps = new StepCollection { ["child"] = new StepMetadata { Type = "DoWork" } }
        };
        var flow = FlowWith("loop1", loop);

        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep());

        var sr = raw.Should().BeOfType<StepResult>().Subject;
        sr.Status.Should().Be(StepStatus.Succeeded);
        sr.DispatchHint.Should().BeNull();
        JsonSerializer.SerializeToElement(sr.Result)
            .GetProperty("iterations").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_StaticList_ReturnsDispatchHintWithChildren()
    {
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

        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));

        var sr = raw.Should().BeOfType<StepResult>().Subject;
        sr.Status.Should().Be(StepStatus.Succeeded);
        sr.DispatchHint.Should().NotBeNull();

        var spawn = sr.DispatchHint!.Spawn;
        spawn.Should().HaveCount(3);
        spawn[0].StepKey.Should().Be("loop1.0.child");
        spawn[1].StepKey.Should().Be("loop1.1.child");
        spawn[2].StepKey.Should().Be("loop1.2.child");
        spawn.Should().AllSatisfy(r => r.StepType.Should().Be("DoWork"));
    }

    [Fact]
    public async Task ExecuteAsync_ChildInputsContainLoopItemAndIndex()
    {
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

        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));

        var spawn = ((StepResult)raw!).DispatchHint!.Spawn;
        var inputs = spawn[0].Inputs;
        inputs["__loopItem"].Should().Be("x");
        inputs["__loopIndex"].Should().Be(0);
        inputs["staticKey"].Should().Be("staticVal");
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_AddsDelayForLaterBuckets()
    {
        var loop = new LoopStepMetadata
        {
            Type = "ForEach",
            ConcurrencyLimit = 2,          // bucket 0 = items 0-1, bucket 1 = items 2-3
            ForEach = new List<object?> { "a", "b", "c", "d" },
            Steps = new StepCollection
            {
                ["child"] = new StepMetadata { Type = "DoWork", RunAfter = new(), Inputs = new Dictionary<string, object?>() }
            }
        };
        var flow = FlowWith("loop1", loop);

        var raw = await _sut.ExecuteAsync(MakeContext(), flow, MakeStep("loop1"));
        var spawn = ((StepResult)raw!).DispatchHint!.Spawn;

        spawn[0].Delay.Should().BeNull();          // bucket 0 item 0
        spawn[1].Delay.Should().BeNull();          // bucket 0 item 1
        spawn[2].Delay.Should().Be(TimeSpan.FromMilliseconds(100));  // bucket 1 item 0
        spawn[3].Delay.Should().Be(TimeSpan.FromMilliseconds(100));  // bucket 1 item 1
    }

    [Fact]
    public async Task ExecuteAsync_JsonArrayTriggerBody_ResolvesItems()
    {
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
        var raw = await _sut.ExecuteAsync(MakeContext(triggerData), flow, MakeStep("loop1"));

        var sr = (StepResult)raw!;
        sr.DispatchHint!.Spawn.Should().HaveCount(3);
    }
}
