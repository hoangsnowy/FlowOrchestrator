using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting.Internal;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// RFC 2.0 C1: a worker killed mid-handler left a claim that never expired, so every redelivery of the step
/// exited as a duplicate and the run stayed Running for ever (stress harness: 23 of 600 runs on SQL Server +
/// Hangfire, 10 on PostgreSQL + Hangfire, 9 on Service Bus). A step is now claimed under a lease that another
/// delivery takes over once it expires, every write of the old attempt is fenced, and a sweep redelivers steps
/// whose lease expired.
/// </summary>
/// <remarks>
/// Two engines share one store and one outputs repository — two replicas. The "dead" worker is an attempt whose
/// handler never returns while nothing renews its lease (no renewal loop runs in these tests; the keeper is
/// driven by hand), and the clock is the store's, so expiry is deterministic.
/// </remarks>
public sealed class StepLeaseTakeoverTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFlowRunStore _store = new();
    private readonly InMemoryOutputsRepository _outputs = new();
    private readonly IFlowDefinition _flow;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly HangingThenSucceedingExecutor _executor = new();

    /// <summary>Builds the one-step flow and starts its run.</summary>
    public StepLeaseTakeoverTests()
    {
        _store.Clock = _clock;
        _flow = Substitute.For<IFlowDefinition>();
        _flow.Id.Returns(Guid.NewGuid());
        _flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = new StepCollection { ["work"] = new StepMetadata { Type = "Work" } }
        });
        _store.StartRunAsync(_flow.Id, "LeaseFlow", _runId, "manual", null, null).GetAwaiter().GetResult();
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Returns a result per invocation: the first call blocks until <see cref="ReleaseFirst"/> — the attempt of a
    /// worker that died, or stalled — and every later call succeeds at once.
    /// </summary>
    private sealed class HangingThenSucceedingExecutor : IStepExecutor
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IStepResult> _firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task FirstEntered => _firstEntered.Task;

        public void ReleaseFirst(IStepResult result) => _firstResult.TrySetResult(result);

        public async ValueTask<IStepResult> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstEntered.TrySetResult();
                return await _firstResult.Task.ConfigureAwait(false);
            }

            return new StepResult { Key = step.Key, Status = StepStatus.Succeeded, Result = new { by = "replacement" } };
        }
    }

    private FlowOrchestratorEngine CreateEngine(StepLeaseKeeper? keeper) => new(
        Substitute.For<IStepDispatcher>(),
        new FlowGraphPlanner(),
        _executor,
        Substitute.For<IFlowStore>(),
        _store,
        _outputs,
        Substitute.For<IExecutionContextAccessor>(),
        new FlowRegistry([_flow]),
        [_store],
        [_store],
        new FlowRunControlOptions(),
        new FlowObservabilityOptions { EnableOpenTelemetry = false },
        new FlowOrchestratorTelemetry(),
        Substitute.For<ILogger<FlowOrchestratorEngine>>(),
        NoopFlowEventNotifier.Instance,
        signalStore: null,
        leaseKeeper: keeper);

    private static StepLeaseKeeper NewKeeper() => new(new FlowRunControlOptions { StepLeaseDuration = LeaseDuration });

    private Task<object?> RunAsync(FlowOrchestratorEngine engine) =>
        engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, _flow, new StepInstance("work", "Work") { RunId = _runId }).AsTask();

    private async Task<string?> StepStatusAsync() => (await _store.GetStepAsync(_runId, "work"))?.Status;

    [Fact]
    public async Task WorkerDiedMidHandler_RedeliveryAfterTheLeaseExpired_RunsTheStepAndClosesTheRun()
    {
        // Arrange — replica A wins the step and its handler never returns; nothing renews A's lease.
        var replicaA = CreateEngine(NewKeeper());
        var replicaB = CreateEngine(NewKeeper());
        var deadAttempt = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));

        // Act — the runtime redelivers the step once the lease has expired.
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        await RunAsync(replicaB);

        // Assert — the step ran again and the run closed.
        Assert.Equal(2, _executor.Calls);
        Assert.Equal(nameof(StepStatus.Succeeded), await StepStatusAsync());
        Assert.Equal("Succeeded", await _store.GetRunStatusAsync(_runId));
        Assert.False(deadAttempt.IsCompleted);
    }

    [Fact]
    public async Task LeasesTurnedOff_ClaimNeverExpires_RedeliveryExitsAsBefore()
    {
        // Arrange — the 1.x behaviour the lease replaces (and the opt-out it keeps): no keeper, a plain claim.
        var replicaA = CreateEngine(keeper: null);
        var replicaB = CreateEngine(keeper: null);
        _ = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));

        // Act
        _clock.Advance(TimeSpan.FromHours(1));
        await RunAsync(replicaB);

        // Assert — the redelivery lost the claim: the stuck run of the stress harness.
        Assert.Equal(1, _executor.Calls);
        Assert.Equal(nameof(StepStatus.Running), await StepStatusAsync());
        Assert.Equal("Running", await _store.GetRunStatusAsync(_runId));
    }

    [Fact]
    public async Task LeaseStillLive_RedeliveryExitsWithoutRunningTheHandler()
    {
        // Arrange
        var replicaA = CreateEngine(NewKeeper());
        var replicaB = CreateEngine(NewKeeper());
        _ = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));

        // Act — a redelivery while A's lease is live (e.g. a Hangfire invisibility timeout shorter than the lease).
        _clock.Advance(LeaseDuration - TimeSpan.FromSeconds(1));
        await RunAsync(replicaB);

        // Assert
        Assert.Equal(1, _executor.Calls);
        Assert.Equal(nameof(StepStatus.Running), await StepStatusAsync());
    }

    [Fact]
    public async Task RenewedLease_IsNotTakenOverWhileItsHandlerRuns()
    {
        // Arrange — A's handler is slow but alive: its keeper renews at 40 s.
        var keeperA = NewKeeper();
        var replicaA = CreateEngine(keeperA);
        var replicaB = CreateEngine(NewKeeper());
        _ = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(40));
        var renewed = await keeperA.RenewAsync(_store);

        // Act — 70 s after the claim: past the original expiry, inside the renewed one.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunAsync(replicaB);

        // Assert
        Assert.Equal(1, renewed);
        Assert.Equal(1, _executor.Calls);
    }

    [Fact]
    public async Task StalledWorkerReturnsAfterTheTakeover_ItsOutcomeIsRejected()
    {
        // Arrange — A stalls past its lease, B takes the step over and succeeds.
        var replicaA = CreateEngine(NewKeeper());
        var replicaB = CreateEngine(NewKeeper());
        var stalled = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        await RunAsync(replicaB);

        // Act — A's handler finally returns, with a different outcome.
        _executor.ReleaseFirst(new StepResult { Key = "work", Status = StepStatus.Failed, FailedReason = "stale attempt" });
        await stalled.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — the fence rejected A's completion: B's outcome stands.
        Assert.Equal(nameof(StepStatus.Succeeded), await StepStatusAsync());
        Assert.Equal("Succeeded", await _store.GetRunStatusAsync(_runId));
    }

    [Fact]
    public async Task RenewalFindsTheLeaseTakenOver_TheReturningAttemptWritesNoOutput()
    {
        // Arrange — B took the step over; A's keeper learns it on its next renewal.
        var keeperA = NewKeeper();
        var replicaA = CreateEngine(keeperA);
        var replicaB = CreateEngine(NewKeeper());
        var stalled = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        await RunAsync(replicaB);
        var outputByB = await _outputs.GetStepOutputAsync(_runId, "work");
        var renewedA = await keeperA.RenewAsync(_store);

        // Act
        _executor.ReleaseFirst(new StepResult { Key = "work", Status = StepStatus.Succeeded, Result = new { by = "stale" } });
        await stalled.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — A discarded its result before writing it.
        Assert.Equal(0, renewedA);
        Assert.Equal(outputByB?.ToString(), (await _outputs.GetStepOutputAsync(_runId, "work"))?.ToString());
    }

    [Fact]
    public async Task CompletedStep_IsNeverTakenOverAfterItsLeaseWouldHaveExpired()
    {
        // Arrange — A completes normally.
        var replicaA = CreateEngine(NewKeeper());
        var replicaB = CreateEngine(NewKeeper());
        var attempt = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        _executor.ReleaseFirst(new StepResult { Key = "work", Status = StepStatus.Succeeded });
        await attempt.WaitAsync(TimeSpan.FromSeconds(30));

        // Act — a late duplicate delivery long after.
        _clock.Advance(TimeSpan.FromHours(1));
        await RunAsync(replicaB);

        // Assert — execute once.
        Assert.Equal(1, _executor.Calls);
    }

    [Fact]
    public async Task Sweep_RedeliversAnExpiredLeaseOncePerBackoff()
    {
        // Arrange — A died mid-handler.
        var replicaA = CreateEngine(NewKeeper());
        _ = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        var dispatcher = Substitute.For<IStepDispatcher>();
        var sweep = new ExpiredStepLeaseRecoverer(_store, new FlowRegistry([_flow]), dispatcher, _outputs, Substitute.For<ILogger>());
        var beforeExpiry = await sweep.RecoverAsync(LeaseDuration, CancellationToken.None);

        // Act
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var first = await sweep.RecoverAsync(LeaseDuration, CancellationToken.None);
        var withinBackoff = await sweep.RecoverAsync(LeaseDuration, CancellationToken.None);
        _clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var afterBackoff = await sweep.RecoverAsync(LeaseDuration, CancellationToken.None);

        // Assert
        Assert.Equal(0, beforeExpiry);
        Assert.Equal(1, first);
        Assert.Equal(0, withinBackoff);
        Assert.Equal(1, afterBackoff);
        await dispatcher.Received(2).EnqueueStepAsync(
            Arg.Any<IExecutionContext>(), _flow, Arg.Is<IStepInstance>(s => s.Key == "work"), CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_IgnoresTerminalStepsAndFinishedRuns()
    {
        // Arrange — the step completed under its lease; the lease would long have expired.
        var replicaA = CreateEngine(NewKeeper());
        var attempt = RunAsync(replicaA);
        await _executor.FirstEntered.WaitAsync(TimeSpan.FromSeconds(30));
        _executor.ReleaseFirst(new StepResult { Key = "work", Status = StepStatus.Succeeded });
        await attempt.WaitAsync(TimeSpan.FromSeconds(30));
        var dispatcher = Substitute.For<IStepDispatcher>();
        var sweep = new ExpiredStepLeaseRecoverer(_store, new FlowRegistry([_flow]), dispatcher, _outputs, Substitute.For<ILogger>());

        // Act
        _clock.Advance(TimeSpan.FromHours(1));
        var redelivered = await sweep.RecoverAsync(LeaseDuration, CancellationToken.None);

        // Assert
        Assert.Equal(0, redelivered);
    }
}
