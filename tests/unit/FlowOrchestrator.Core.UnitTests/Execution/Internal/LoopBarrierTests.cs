using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the loop-completion barrier introduced for issue #169: a ForEach step stays
/// <see cref="StepStatus.Running"/> until every iteration of its body is terminal, so a step
/// declaring <c>RunAfter = { loop: [Succeeded] }</c> cannot run alongside the loop body.
/// </summary>
public class LoopBarrierTests
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    private static IFlowDefinition SingleLoopFlow() => FlowWith(new StepCollection
    {
        ["loop"] = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection
            {
                ["wait"] = new StepMetadata { Type = "WaitForSignal" },
                ["consume"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["wait"] = [StepStatus.Succeeded] }
                }
            }
        },
        ["after"] = new StepMetadata
        {
            Type = "Echo",
            RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
        }
    });

    private static JsonElement Iterations(int count) =>
        JsonSerializer.SerializeToElement(new { iterations = count }, _webOptions);

    [Fact]
    public void EnclosingLoopKeys_TopLevelStep_ReturnsEmpty()
    {
        // Arrange

        // Act
        var keys = LoopBarrier.EnclosingLoopKeys("robot_callback_success");

        // Assert
        Assert.Empty(keys);
    }

    [Fact]
    public void EnclosingLoopKeys_LoopChild_ReturnsTheLoopKey()
    {
        // Arrange

        // Act
        var keys = LoopBarrier.EnclosingLoopKeys("scan_process.3.wait_robot_goto");

        // Assert
        Assert.Equal(["scan_process"], keys);
    }

    [Fact]
    public void EnclosingLoopKeys_NestedLoopChild_ReturnsInnermostFirst()
    {
        // Arrange

        // Act
        var keys = LoopBarrier.EnclosingLoopKeys("outer.1.inner.0.child");

        // Assert — inner before outer, so settling the inner loop can settle the outer one
        // in the same pass.
        Assert.Equal(["outer.1.inner", "outer"], keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void TryReadIterationCount_LoopOutputShape_ReadsCount(int expected)
    {
        // Arrange
        var output = Iterations(expected);

        // Act
        var read = LoopBarrier.TryReadIterationCount(output, out var iterations);

        // Assert
        Assert.True(read);
        Assert.Equal(expected, iterations);
    }

    [Fact]
    public void TryReadIterationCount_DictionaryOutput_ReadsCount()
    {
        // Arrange — custom stores may hand back the raw graph instead of a JsonElement.
        var output = new Dictionary<string, object?> { ["Iterations"] = 4 };

        // Act
        var read = LoopBarrier.TryReadIterationCount(output, out var iterations);

        // Assert
        Assert.True(read);
        Assert.Equal(4, iterations);
    }

    [Fact]
    public void TryReadIterationCount_UnrelatedOutput_ReturnsFalse()
    {
        // Arrange
        var output = JsonSerializer.SerializeToElement(new { note = "not a loop" }, _webOptions);

        // Act
        var read = LoopBarrier.TryReadIterationCount(output, out var iterations);

        // Assert
        Assert.False(read);
        Assert.Equal(0, iterations);
    }

    [Fact]
    public void AllIterationsSettled_ChildStillParked_ReturnsFalse()
    {
        // Arrange
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded,
            ["loop.1.wait"] = StepStatus.Pending
        };

        // Act
        var settled = LoopBarrier.AllIterationsSettled(SingleLoopFlow(), "loop", 2, statuses);

        // Assert
        Assert.False(settled);
    }

    [Fact]
    public void AllIterationsSettled_SkippedChildCounts_ReturnsTrue()
    {
        // Arrange — a failed waiter blocks its consumer, which the engine records as Skipped;
        // that is terminal, so the barrier must settle instead of deadlocking.
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded,
            ["loop.1.wait"] = StepStatus.Failed,
            ["loop.1.consume"] = StepStatus.Skipped
        };

        // Act
        var settled = LoopBarrier.AllIterationsSettled(SingleLoopFlow(), "loop", 2, statuses);

        // Assert
        Assert.True(settled);
    }

    [Fact]
    public void AllIterationsSettled_IterationNotStartedYet_ReturnsFalse()
    {
        // Arrange — iteration 1 carries a dispatch delay and has no status row yet.
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded
        };

        // Act
        var settled = LoopBarrier.AllIterationsSettled(SingleLoopFlow(), "loop", 2, statuses);

        // Assert
        Assert.False(settled);
    }

    [Fact]
    public async Task SettleAsync_AllIterationsTerminal_WritesSucceededForTheLoopStep()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "loop").Returns(ValueTask.FromResult<object?>(Iterations(2)));
        var runStore = Substitute.For<IFlowRunStore>();
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded,
            ["loop.1.wait"] = StepStatus.Succeeded,
            ["loop.1.consume"] = StepStatus.Succeeded
        };

        // Act
        var settled = await LoopBarrier.SettleAsync(
            SingleLoopFlow(), runId, ["loop"], statuses, outputs, runStore);

        // Assert
        Assert.Equal(["loop"], settled);
        await runStore.Received(1).RecordStepCompleteAsync(
            runId, "loop", nameof(StepStatus.Succeeded), """{"iterations":2}""", null);
    }

    [Fact]
    public async Task SettleAsync_IterationStillRunning_LeavesTheLoopParked()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "loop").Returns(ValueTask.FromResult<object?>(Iterations(2)));
        var runStore = Substitute.For<IFlowRunStore>();
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Running,
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded,
            ["loop.1.wait"] = StepStatus.Pending
        };

        // Act
        var settled = await LoopBarrier.SettleAsync(
            SingleLoopFlow(), runId, ["loop"], statuses, outputs, runStore);

        // Assert
        Assert.Empty(settled);
        await runStore.DidNotReceiveWithAnyArgs().RecordStepCompleteAsync(
            default, default!, default!, default, default);
    }

    [Fact]
    public async Task SettleAsync_LoopAlreadySettled_IsANoOp()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var outputs = Substitute.For<IOutputsRepository>();
        var runStore = Substitute.For<IFlowRunStore>();
        var statuses = new Dictionary<string, StepStatus>
        {
            ["loop"] = StepStatus.Succeeded,
            ["loop.0.wait"] = StepStatus.Succeeded,
            ["loop.0.consume"] = StepStatus.Succeeded
        };

        // Act
        var settled = await LoopBarrier.SettleAsync(
            SingleLoopFlow(), runId, ["loop"], statuses, outputs, runStore);

        // Assert
        Assert.Empty(settled);
        await runStore.DidNotReceiveWithAnyArgs().RecordStepCompleteAsync(
            default, default!, default!, default, default);
    }

    [Fact]
    public async Task SettleAsync_NestedLoops_SettlesInnerThenOuterInOnePass()
    {
        // Arrange — the outer loop's only child is itself a loop, so the outer barrier can only
        // settle once the inner one has been written in the same pass.
        var runId = Guid.NewGuid();
        var flow = FlowWith(new StepCollection
        {
            ["outer"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection
                {
                    ["inner"] = new LoopStepMetadata
                    {
                        Type = "ForEach",
                        Steps = new StepCollection { ["child"] = new StepMetadata { Type = "Echo" } }
                    }
                }
            }
        });

        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "outer").Returns(ValueTask.FromResult<object?>(Iterations(1)));
        outputs.GetStepOutputAsync(runId, "outer.0.inner").Returns(ValueTask.FromResult<object?>(Iterations(2)));
        var runStore = Substitute.For<IFlowRunStore>();

        var statuses = new Dictionary<string, StepStatus>
        {
            ["outer"] = StepStatus.Running,
            ["outer.0.inner"] = StepStatus.Running,
            ["outer.0.inner.0.child"] = StepStatus.Succeeded,
            ["outer.0.inner.1.child"] = StepStatus.Succeeded
        };

        // Act
        var settled = await LoopBarrier.SettleAsync(
            flow, runId, LoopBarrier.EnclosingLoopKeys("outer.0.inner.1.child"), statuses, outputs, runStore);

        // Assert
        Assert.Equal(["outer.0.inner", "outer"], settled);
    }

    [Fact]
    public void RunningLoopKeys_ReturnsOnlyScopedStepsDeepestFirst()
    {
        // Arrange
        var flow = FlowWith(new StepCollection
        {
            ["outer"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection
                {
                    ["inner"] = new LoopStepMetadata
                    {
                        Type = "ForEach",
                        Steps = new StepCollection { ["child"] = new StepMetadata { Type = "Echo" } }
                    }
                }
            },
            ["plain"] = new StepMetadata { Type = "Echo" }
        });

        var statuses = new Dictionary<string, StepStatus>
        {
            ["outer"] = StepStatus.Running,
            ["outer.0.inner"] = StepStatus.Running,
            ["plain"] = StepStatus.Running
        };

        // Act
        var keys = LoopBarrier.RunningLoopKeys(flow, statuses);

        // Assert — a plain Running step is a worker executing it, not a barrier.
        Assert.Equal(["outer.0.inner", "outer"], keys);
    }
}
