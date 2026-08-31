using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Multi-worker coverage for the ForEach completion barrier: two iterations finishing at the same
/// instant on different workers, and at-least-once redelivery of a loop message that was already
/// processed. Settling is documented as idempotent rather than exclusive, so both cases must
/// converge on exactly one downstream dispatch and one settled loop step.
/// </summary>
public sealed class LoopBarrierConcurrencyTests
{
    /// <summary>Generous budget so a contended CI box cannot trip the rendezvous.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private static IFlowDefinition MakeLoopFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "a", "b" },
                    ConcurrencyLimit = 2,
                    Steps = new StepCollection
                    {
                        ["work"] = new StepMetadata { Type = "Echo" }
                    }
                },
                ["after_loop"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IStepResult ParkedLoop() => new StepResult
    {
        Key = "loop",
        Status = StepStatus.Running,
        Result = new { iterations = 2 },
        DispatchHint = new StepDispatchHint(
        [
            new StepDispatchRequest("loop.0.work", "Echo", new Dictionary<string, object?>()),
            new StepDispatchRequest("loop.1.work", "Echo", new Dictionary<string, object?>())
        ])
    };

    private static IStepResult Succeeded(string stepKey) =>
        new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };

    [Fact]
    public async Task TwoIterationsFinishingSimultaneously_dispatchTheDownstreamStepExactlyOnce()
    {
        // Arrange - both iteration handlers rendezvous on a countdown before returning, so their
        // continuations (and therefore both settle passes) genuinely overlap.
        using var rendezvous = new CountdownEvent(2);
        var flow = MakeLoopFlow();
        var harness = new LoopBarrierEngineHarness(flow, stepKey =>
        {
            if (stepKey == "loop")
            {
                return ParkedLoop();
            }

            if (stepKey.StartsWith("loop.", StringComparison.Ordinal))
            {
                rendezvous.Signal();
                Assert.True(rendezvous.Wait(GateTimeout), "iteration handlers never met at the rendezvous");
            }

            return Succeeded(stepKey);
        });

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");

        // Act
        await harness.RunConcurrentlyAsync("loop.0.work", "loop.1.work");
        await harness.DrainAsync();

        // Assert - the duplicate settle write is harmless, but the dispatch ledger must still
        // admit only one downstream enqueue, and the run must complete exactly once.
        Assert.Equal(1, harness.Enqueued.Count(k => k == "after_loop"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "after_loop"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RedeliveredIterationMessage_afterTheLoopSettled_isRejectedByTheClaimGuard()
    {
        // Arrange - run to completion so the barrier has already settled the loop.
        var flow = MakeLoopFlow();
        var harness = new LoopBarrierEngineHarness(
            flow, stepKey => stepKey == "loop" ? ParkedLoop() : Succeeded(stepKey));

        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
        var attemptsBefore = await AttemptCountAsync(harness, runId, "loop.0.work");

        // Act - the broker redelivers an iteration message that was already processed.
        await harness.RedeliverAsync(runId, "loop.0.work", "Echo");

        // Assert - the execute-time claim is still held, so the redelivery is a no-op: no extra
        // attempt, no second settle, no second downstream dispatch.
        Assert.Equal(attemptsBefore, await AttemptCountAsync(harness, runId, "loop.0.work"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "after_loop"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.0.work"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RedeliveredLoopMessage_whileParkedOnTheBarrier_doesNotFanOutASecondTime()
    {
        // Arrange - the loop fanned out and is parked; the claim taken at fan-out is never
        // released for a Running result.
        var flow = MakeLoopFlow();
        var harness = new LoopBarrierEngineHarness(
            flow, stepKey => stepKey == "loop" ? ParkedLoop() : Succeeded(stepKey));

        var runId = await harness.TriggerAsync();
        await harness.RunKeyAsync("loop");

        // Act
        await harness.RedeliverAsync(runId, "loop", "ForEach");
        await harness.DrainAsync();

        // Assert - a second fan-out would double-dispatch every iteration and could double-count
        // the barrier; the claim guard must swallow the redelivery entirely.
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.0.work"));
        Assert.Equal(1, harness.Enqueued.Count(k => k == "loop.1.work"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "loop"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    private static async Task<int> AttemptCountAsync(LoopBarrierEngineHarness harness, Guid runId, string stepKey)
    {
        var detail = await harness.Store.GetRunDetailAsync(runId);
        return detail!.Steps!.First(s => s.StepKey == stepKey).AttemptCount;
    }
}
