using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// A user cancel that lands between the engine's read of the run-control record and its timeout latch must
/// win: the store refuses <see cref="IFlowRunControlStore.MarkTimedOutAsync"/> once a cancel is recorded,
/// and the engine has to report the run as Cancelled too, not TimedOut.
/// </summary>
public sealed class CancelBeatsRacingTimeoutTests
{
    /// <summary>
    /// Control-store decorator that records a user cancel immediately before forwarding the engine's timeout
    /// latch, i.e. the cancel wins the race by a hair.
    /// </summary>
    private sealed class CancelLandsFirstControlStore(InMemoryFlowRunStore inner) : IFlowRunControlStore
    {
        public Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
            => inner.ConfigureRunAsync(runId, flowId, triggerKey, idempotencyKey, timeoutAtUtc);

        public Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId) => inner.GetRunControlAsync(runId);

        public Task<bool> RequestCancelAsync(Guid runId, string? reason) => inner.RequestCancelAsync(runId, reason);

        public async Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
        {
            await inner.RequestCancelAsync(runId, "cancelled by operator");
            return await inner.MarkTimedOutAsync(runId, reason);
        }

        public Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc) => inner.ExtendDeadlineAsync(runId, newTimeoutAtUtc);

        public Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
            => inner.FindRunIdByIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey);

        public Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => inner.TryRegisterIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, runId);

        public Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
            => inner.ReleaseIdempotencyKeyAsync(flowId, triggerKey, idempotencyKey, runId);
    }

    private static IFlowDefinition TwoStepFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection
            {
                ["first"] = new StepMetadata { Type = "Echo" },
                ["second"] = new StepMetadata
                {
                    Type = "Echo",
                    RunAfter = new RunAfterCollection { ["first"] = [StepStatus.Succeeded] }
                }
            }
        });
        return flow;
    }

    private static IStepResult Succeeded(string key) => new StepResult { Key = key, Status = StepStatus.Succeeded };

    [Fact]
    public async Task Continuation_reports_Cancelled_when_a_cancel_beats_the_timeout_latch()
    {
        // Arrange — the run's deadline has lapsed, so the continuation after "first" decides to time it out;
        // the user's cancel lands just before the latch.
        var harness = new LoopBarrierEngineHarness(TwoStepFlow(), Succeeded, controlStore: s => new CancelLandsFirstControlStore(s));
        var runId = await harness.TriggerAsync();
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.RunKeyAsync("first");

        // Assert
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.True(control!.CancelRequested);
        Assert.Null(control.TimedOutAtUtc);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
        Assert.DoesNotContain("second", harness.Enqueued);
    }

    [Fact]
    public async Task Sweep_reports_Cancelled_when_a_cancel_beats_the_timeout_latch()
    {
        // Arrange — nothing in flight after "first" was dispatched but never run; the sweep finds the lapsed
        // deadline, and the user's cancel lands just before its latch.
        var harness = new LoopBarrierEngineHarness(TwoStepFlow(), Succeeded, controlStore: s => new CancelLandsFirstControlStore(s));
        var runId = await harness.TriggerAsync();
        await harness.Store.ReleaseDispatchAsync(runId, "first");
        await harness.Store.ExtendDeadlineAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));

        // Act
        await harness.Engine.EnforceDueTimeoutsAsync();

        // Assert
        var control = await harness.Store.GetRunControlAsync(runId);
        Assert.NotNull(control);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.Equal("Cancelled", await harness.RunStatusAsync(runId));
    }
}
