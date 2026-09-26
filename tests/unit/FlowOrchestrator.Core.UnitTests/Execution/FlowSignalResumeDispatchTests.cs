using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Covers how <see cref="FlowSignalDispatcher"/> chooses between an immediate enqueue and a short
/// delayed nudge when waking a parked <c>WaitForSignal</c> step (issue #188).
/// </summary>
/// <remarks>
/// The distinction is not cosmetic: on the Hangfire runtime <c>ScheduleStepAsync</c> routes through
/// <c>BackgroundJob.Schedule</c> and lands in the Scheduled set, where the job waits for the next
/// <c>DelayedJobScheduler</c> tick — 15 seconds by default. Enqueuing immediately bypasses that poll
/// entirely, so these tests lock in which path is taken under each claim state.
/// </remarks>
public sealed class FlowSignalResumeDispatchTests
{
    private const string StepKey = "wait_robot_polish";
    private const string SignalName = "robot_done";

    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _flowId = Guid.NewGuid();
    private readonly IStepDispatcher _stepDispatcher = Substitute.For<IStepDispatcher>();
    private readonly IFlowSignalStore _signalStore = Substitute.For<IFlowSignalStore>();
    private readonly IFlowRunStore _runStore = Substitute.For<IFlowRunStore>();
    private readonly IFlowRepository _flowRepository = Substitute.For<IFlowRepository>();
    private readonly IOutputsRepository _outputsRepository = Substitute.For<IOutputsRepository>();

    /// <summary>Wires the happy-path substitutes shared by every case: a delivered signal on a resolvable run and flow.</summary>
    public FlowSignalResumeDispatchTests()
    {
        _signalStore.DeliverSignalAsync(_runId, SignalName, "{}", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.Delivered, StepKey, DateTimeOffset.UtcNow)));

        _runStore.GetRunAsync(_runId).Returns(Task.FromResult<FlowRunRecord?>(new FlowRunRecord
        {
            Id = _runId,
            FlowId = _flowId,
            FlowName = "Polish Process",
            Status = "Running",
            TriggerKey = "manual",
            StartedAt = DateTimeOffset.UtcNow,
        }));

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(_flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                [StepKey] = new StepMetadata { Type = "WaitForSignal" }
            }
        });

        _flowRepository.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));
    }

    [Fact]
    public async Task Resume_enqueues_immediately_when_the_step_claim_is_already_released()
    {
        // Arrange — the normal case: the step has been parked long enough that the invocation
        // which parked it has completed and released its execution claim.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(false));
        var dispatcher = CreateDispatcher(runtimeStore);

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        await _stepDispatcher.Received(1).EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
        await _stepDispatcher.DidNotReceive().ScheduleStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_is_delayed_when_the_parking_invocation_still_holds_the_claim()
    {
        // Arrange — the narrow race: the waiter row is committed (so delivery succeeds) but the
        // invocation that parked the step has not yet reached ReleaseStepClaimAsync. Enqueuing now
        // would lose TryClaimStepAsync and be dropped silently.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(true));
        var dispatcher = CreateDispatcher(runtimeStore);

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        await _stepDispatcher.Received(1).ScheduleStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(),
            TimeSpan.FromMilliseconds(500), Arg.Any<CancellationToken>());
        await _stepDispatcher.DidNotReceive().EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_enqueues_immediately_when_another_step_holds_a_claim_but_this_one_does_not()
    {
        // Arrange — a sibling branch is executing concurrently. Only OUR step key gates the choice,
        // so an unrelated claim must not force the slow path.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(false));
        runtimeStore.IsStepClaimedAsync(_runId, "some_other_step").Returns(Task.FromResult(true));
        var dispatcher = CreateDispatcher(runtimeStore);

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        await _stepDispatcher.Received(1).EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
        await _stepDispatcher.DidNotReceive().ScheduleStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_enqueues_immediately_when_no_runtime_store_is_registered()
    {
        // Arrange — no runtime store means FlowOrchestratorEngine.RunStepAsync skips its claim guard
        // entirely (it is guarded by the same `is not null` check), so there is no TryClaimStepAsync
        // for the resume to lose. The immediate path is provably safe here, and delaying would impose
        // the scheduled-set penalty on precisely the configuration that cannot race.
        var dispatcher = CreateDispatcher(runtimeStore: null);

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        await _stepDispatcher.Received(1).EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>());
        await _stepDispatcher.DidNotReceive().ScheduleStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_is_dispatched_with_an_uncancellable_token_even_when_the_caller_has_gone_away()
    {
        // Arrange — the dashboard endpoint passes http.RequestAborted, which trips as soon as the
        // caller disconnects. The signal is already durably delivered by then, so the nudge must not
        // inherit that token: InMemoryStepDispatcher.EnqueueStepAsync honours it and would fail the
        // channel write, stranding the step until its safety net (24h without timeoutSeconds).
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(false));
        var dispatcher = CreateDispatcher(runtimeStore);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        CancellationToken received = default;
        _stepDispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Do<CancellationToken>(t => received = t))
            .Returns(new ValueTask<string?>((string?)null));

        // Act
        var result = await dispatcher.DispatchAsync(_runId, SignalName, "{}", cts.Token);

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.False(received.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task Delayed_resume_is_also_dispatched_with_an_uncancellable_token()
    {
        // Arrange — same reasoning on the delayed branch. InMemoryStepDispatcher already drops the
        // token inside ScheduleStepAsync, but Hangfire and Service Bus do not, so the guarantee has
        // to hold at this call site rather than relying on each adapter.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(true));
        var dispatcher = CreateDispatcher(runtimeStore);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        CancellationToken received = default;
        _stepDispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(),
                Arg.Any<TimeSpan>(), Arg.Do<CancellationToken>(t => received = t))
            .Returns(new ValueTask<string?>((string?)null));

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}", cts.Token);

        // Assert
        Assert.Equal(CancellationToken.None, received);
    }

    [Fact]
    public async Task Resume_falls_back_to_the_delayed_path_when_claim_state_cannot_be_read()
    {
        // Arrange — a transient storage fault must not be resolved by guessing "not claimed",
        // because guessing wrong strands the step until its safety-net invocation.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).ThrowsAsync(new InvalidOperationException("storage down"));
        var dispatcher = CreateDispatcher(runtimeStore);

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        await _stepDispatcher.Received(1).ScheduleStepAsync(
            Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(),
            TimeSpan.FromMilliseconds(500), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delivery_is_reported_as_succeeded_even_when_the_immediate_dispatch_throws()
    {
        // Arrange — the signal is already durably persisted before the nudge, so a dispatcher
        // failure may cost latency but must never surface as a failed delivery to the caller.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(false));
        _stepDispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("queue unavailable"));
        var dispatcher = CreateDispatcher(runtimeStore);

        // Act
        var result = await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        Assert.Equal(SignalDeliveryStatus.Delivered, result.Status);
        Assert.Equal(StepKey, result.StepKey);
    }

    [Fact]
    public async Task Immediate_resume_does_not_post_date_the_step_scheduled_time()
    {
        // Arrange — ScheduledTime feeds dashboard/observability. On the immediate path it must not
        // advertise a future time the step is not actually waiting for.
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.IsStepClaimedAsync(_runId, StepKey).Returns(Task.FromResult(false));
        var dispatcher = CreateDispatcher(runtimeStore);
        IStepInstance? dispatched = null;
        _stepDispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Do<IStepInstance>(s => dispatched = s), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>((string?)null));
        var before = DateTimeOffset.UtcNow;

        // Act
        await dispatcher.DispatchAsync(_runId, SignalName, "{}");

        // Assert
        Assert.NotNull(dispatched);
        // Upper bound with NO slack: the immediate path must stamp UtcNow, not UtcNow + ResumeDelay.
        // A generous window here would pass for the post-dated value this test exists to reject.
        Assert.InRange(dispatched!.ScheduledTime, before, DateTimeOffset.UtcNow);
    }

    /// <summary>Builds the subject under test with the shared substitutes and the supplied runtime store.</summary>
    private FlowSignalDispatcher CreateDispatcher(IFlowRunRuntimeStore? runtimeStore) => new(
        _signalStore,
        _runStore,
        _flowRepository,
        _stepDispatcher,
        _outputsRepository,
        telemetry: null,
        runtimeStores: runtimeStore is null ? Array.Empty<IFlowRunRuntimeStore>() : new[] { runtimeStore },
        logger: null);
}
