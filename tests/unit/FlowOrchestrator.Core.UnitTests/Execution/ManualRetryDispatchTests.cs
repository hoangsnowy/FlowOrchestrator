using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// RFC 2.0 S4 / P-1(c): a manual retry resets the step with a compare-and-swap on <c>Failed</c> and
/// dispatches the new attempt through the runtime instead of running the handler on the caller's thread.
/// </summary>
public sealed class ManualRetryDispatchTests
{
    /// <summary>Generous budget so a contended CI box cannot trip the rendezvous.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private static IFlowDefinition MakeSingleStepFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection { ["step1"] = new StepMetadata { Type = "Work" } }
        });
        return flow;
    }

    /// <summary>
    /// A handler that fails its first execution and succeeds afterwards, counting executions. Every later
    /// execution announces itself on <see cref="RetryStarted"/> and then waits for <see cref="ReleaseRetries"/>.
    /// </summary>
    private sealed class FlakyHandler
    {
        private readonly TaskCompletionSource _retryGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executions;

        /// <summary>How many times the handler ran.</summary>
        public int Executions => Volatile.Read(ref _executions);

        /// <summary>Completes when an execution after the first one has started.</summary>
        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a third execution — a second, duplicate retry — has started.</summary>
        public TaskCompletionSource SecondRetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets every held execution finish, and every later one run straight through.</summary>
        public void ReleaseRetries() => _retryGate.TrySetResult();

        /// <summary>The executor callback for the harness.</summary>
        public IStepResult Run(string stepKey)
        {
            if (Interlocked.Increment(ref _executions) == 1)
            {
                return new StepResult { Key = stepKey, Status = StepStatus.Failed, FailedReason = "boom" };
            }

            RetryStarted.TrySetResult();
            if (Volatile.Read(ref _executions) >= 3)
            {
                SecondRetryStarted.TrySetResult();
            }

            // Synchronous wait: the harness's executor callback is synchronous; the 30 s budget only
            // guards against a test bug that never releases the gate.
            Assert.True(_retryGate.Task.Wait(GateTimeout), "the test never released the retried execution");
            return new StepResult { Key = stepKey, Status = StepStatus.Succeeded, Result = new { ok = true } };
        }
    }

    private static async Task<(LoopBarrierEngineHarness Harness, FaultInjectingRunStore Faults, FlakyHandler Handler, IFlowDefinition Flow, Guid RunId)> FailedRunAsync(bool holdRetries = false)
    {
        var flow = MakeSingleStepFlow();
        var handler = new FlakyHandler();
        if (!holdRetries)
        {
            handler.ReleaseRetries();
        }

        FaultInjectingRunStore? faults = null;
        var harness = new LoopBarrierEngineHarness(flow, handler.Run, runStore: store => faults = new FaultInjectingRunStore(store));
        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        Assert.Equal("Failed", await harness.RunStatusAsync(runId));
        return (harness, faults!, handler, flow, runId);
    }

    [Fact]
    public async Task TwoConcurrentRetriesOfOneFailedStep_acceptOne_andRunTheHandlerOnce()
    {
        // Arrange - the RFC S4 interleaving, forced with gates rather than timing:
        //   1. R2 checks the step (Failed) and is held right in front of the store's reset.
        //   2. R1 checks the step (Failed), resets it, and gets as far as it goes — dispatched (2.0), or
        //      claimed with its handler running (before 2.0, when the retry ran inline).
        //   3. R2 is released into its reset. Before 2.0 that reset was unconditional: it deleted R1's
        //      claim, claimed the step itself and ran the handler a second time, concurrently.
        var (harness, faults, handler, flow, runId) = await FailedRunAsync(holdRetries: true);
        var resets = 0;
        var r2AtReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseR2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        faults.BeforeRetryStep = async (_, _) =>
        {
            if (Interlocked.Increment(ref resets) == 1)
            {
                r2AtReset.TrySetResult();
                await releaseR2.Task.WaitAsync(GateTimeout);
            }
        };

        // Act
        var r2 = Task.Run(() => harness.Engine.RetryStepAsync(flow.Id, runId, "step1").AsTask());
        await r2AtReset.Task.WaitAsync(GateTimeout);
        var r1 = Task.Run(() => harness.Engine.RetryStepAsync(flow.Id, runId, "step1").AsTask());
        await Task.WhenAny(r1, handler.RetryStarted.Task).WaitAsync(GateTimeout);
        releaseR2.TrySetResult();
        await Task.WhenAny(r2, handler.SecondRetryStarted.Task).WaitAsync(GateTimeout);
        handler.ReleaseRetries();
        var results = await Task.WhenAll(r1, r2).WaitAsync(GateTimeout);
        faults.BeforeRetryStep = null;
        await harness.DrainAsync();

        // Assert - one original execution plus exactly one retried execution.
        Assert.Equal(2, handler.Executions);
        Assert.Equal(StepRetryOutcome.Accepted, Assert.IsType<StepRetryResult>(results[0]).Outcome);
        Assert.Equal(StepRetryOutcome.Conflict, Assert.IsType<StepRetryResult>(results[1]).Outcome);
        Assert.Equal(2, harness.Enqueued.Count(k => k == "step1"));
        Assert.Equal(StepStatus.Succeeded.ToString(), await harness.StepStatusAsync(runId, "step1"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task AcceptedRetry_returnsBeforeTheHandlerRuns_andTheDispatchedAttemptRunsIt()
    {
        // Arrange
        var (harness, _, handler, flow, runId) = await FailedRunAsync();

        // Act
        var result = await harness.Engine.RetryStepAsync(flow.Id, runId, "step1");
        var executionsWhenRetryReturned = handler.Executions;
        var queuedWhenRetryReturned = harness.PendingKeys;
        await harness.DrainAsync();

        // Assert - the call only reset and dispatched; the runtime ran the attempt afterwards.
        Assert.Equal(StepRetryOutcome.Accepted, Assert.IsType<StepRetryResult>(result).Outcome);
        Assert.Equal(1, executionsWhenRetryReturned);
        Assert.Equal(["step1"], queuedWhenRetryReturned);
        Assert.Equal(2, handler.Executions);
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }

    [Fact]
    public async Task RetryOfAStepThatIsNotFailed_isRejected_withoutTouchingTheRun()
    {
        // Arrange - a run that succeeded outright.
        var flow = MakeSingleStepFlow();
        var executions = 0;
        var harness = new LoopBarrierEngineHarness(flow, key =>
        {
            Interlocked.Increment(ref executions);
            return new StepResult { Key = key, Status = StepStatus.Succeeded };
        });
        var runId = await harness.TriggerAsync();
        await harness.DrainAsync();
        var controlBefore = await harness.Store.GetRunControlAsync(runId);

        // Act
        var result = await harness.Engine.RetryStepAsync(flow.Id, runId, "step1");
        await harness.DrainAsync();

        // Assert
        Assert.Equal(StepRetryOutcome.NotFailed, Assert.IsType<StepRetryResult>(result).Outcome);
        Assert.Equal(1, executions);
        Assert.Equal(1, harness.Enqueued.Count(k => k == "step1"));
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
        Assert.True(await harness.Store.IsStepClaimedAsync(runId, "step1"));
        Assert.Equal(controlBefore!.TimeoutAtUtc, (await harness.Store.GetRunControlAsync(runId))!.TimeoutAtUtc);
    }

    [Fact]
    public async Task RetryWhoseDispatchThrows_putsTheStepBackToFailed_andRethrows()
    {
        // Arrange
        var (harness, _, handler, flow, runId) = await FailedRunAsync();
        harness.FailNextDispatch(new InvalidOperationException("broker down"));

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Engine.RetryStepAsync(flow.Id, runId, "step1").AsTask());
        var stepAfterFailure = await harness.StepRecordAsync(runId, "step1");
        var runAfterFailure = await harness.RunStatusAsync(runId);
        var retriedAgain = await harness.Engine.RetryStepAsync(flow.Id, runId, "step1");
        await harness.DrainAsync();

        // Assert - nothing was left Running with nothing queued; the operator could simply retry again.
        Assert.Equal("broker down", thrown.Message);
        Assert.Equal(StepStatus.Failed.ToString(), stepAfterFailure!.Status);
        Assert.Contains("boom", stepAfterFailure.ErrorMessage);
        Assert.Equal("Failed", runAfterFailure);
        Assert.Equal(StepRetryOutcome.Accepted, Assert.IsType<StepRetryResult>(retriedAgain).Outcome);
        Assert.Equal(2, handler.Executions);
        Assert.Equal("Succeeded", await harness.RunStatusAsync(runId));
    }
}
