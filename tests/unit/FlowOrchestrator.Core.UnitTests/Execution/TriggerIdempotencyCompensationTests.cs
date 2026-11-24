using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The compensation <c>TriggerAsync</c> runs when it registered an idempotency key and then failed before
/// dispatching any entry step: the dead run is cancel-latched and completed <c>Failed</c>, and the key is
/// released — while a trigger that already dispatched an entry keeps its key, because its run is alive.
/// </summary>
public sealed class TriggerIdempotencyCompensationTests
{
    private static readonly Dictionary<string, string> Headers = new() { ["Idempotency-Key"] = "order-1" };

    private static IFlowDefinition FlowOf(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = steps
        });
        return flow;
    }

    private static IStepResult Ok(string key) => new StepResult { Key = key, Status = StepStatus.Succeeded };

    private static TriggerContext ContextFor(IFlowDefinition flow, Guid runId) => new()
    {
        RunId = runId,
        Flow = flow,
        Trigger = new Trigger("manual", "Manual", null, headers: Headers)
    };

    [Fact]
    public async Task FailedEntryDispatch_DeadRunIsFailedAndCancelled_AndAStrayDeliveryIsSkipped()
    {
        // Arrange
        var flow = FlowOf(new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        var deadRunId = Guid.NewGuid();
        harness.FailNextDispatch(new InvalidOperationException("broker unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.Engine.TriggerAsync(ContextFor(flow, deadRunId)));

        // Act - the broker had in fact accepted the message: it is delivered for the dead run.
        await harness.RedeliverAsync(deadRunId, "a", "Echo");

        // Assert
        Assert.Equal("Failed", await harness.RunStatusAsync(deadRunId));
        Assert.True((await harness.Store.GetRunControlAsync(deadRunId))?.CancelRequested);
        Assert.Equal("Skipped", await harness.StepStatusAsync(deadRunId, "a"));
        Assert.Null(await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", "order-1"));
    }

    [Fact]
    public async Task RetryAfterFailedEntryDispatch_StartsANewRun()
    {
        // Arrange
        var flow = FlowOf(new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        var deadRunId = Guid.NewGuid();
        harness.FailNextDispatch(new InvalidOperationException("broker unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.Engine.TriggerAsync(ContextFor(flow, deadRunId)));

        // Act
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;
        await harness.DrainAsync();

        // Assert
        Assert.NotNull(retry);
        Assert.False(retry.Duplicate);
        Assert.NotEqual(deadRunId, retry.RunId);
        Assert.Equal("Succeeded", await harness.RunStatusAsync(retry.RunId!.Value));
        Assert.Equal(retry.RunId, await harness.Store.FindRunIdByIdempotencyKeyAsync(flow.Id, "manual", "order-1"));
    }

    [Fact]
    public async Task FailureAfterAnEntryWasDispatched_KeepsTheKey_SoARetryIsADuplicateOfTheLiveRun()
    {
        // Arrange - two entry steps; the first dispatch lands, the second throws.
        var flow = FlowOf(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "Echo" },
            ["b"] = new StepMetadata { Type = "Echo" }
        });
        var harness = new LoopBarrierEngineHarness(flow, Ok);
        var liveRunId = Guid.NewGuid();
        harness.FailDispatchOf("b", new InvalidOperationException("broker unavailable"));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.Engine.TriggerAsync(ContextFor(flow, liveRunId)));
        var retry = await harness.Engine.TriggerAsync(ContextFor(flow, Guid.NewGuid())) as FlowTriggerResult;

        // Assert
        Assert.NotNull(retry);
        Assert.True(retry.Duplicate);
        Assert.Equal(liveRunId, retry.RunId);
        Assert.Equal("Running", await harness.RunStatusAsync(liveRunId));
        Assert.Equal(["a"], harness.Enqueued);
    }
}
