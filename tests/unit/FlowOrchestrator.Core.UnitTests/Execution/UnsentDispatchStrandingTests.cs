using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The stress-harness finding of 2026-11-24 (crash-recovery, SQL Server + Service Bus, 3 replicas, 6 kills):
/// 29 dispatch-ledger rows without a runtime job id, 26 of them stranding their run for ever. The engine
/// records the ledger row <em>before</em> the runtime send; a send that never returns — the process was
/// killed mid-send, or the send hung inside the broker client's retry loop — leaves the row unannotated,
/// and startup recovery skips every ledgered key.
/// </summary>
/// <remarks>
/// A two-step flow <c>a → b</c> on the real in-memory store: <c>a</c> succeeds and the continuation hands
/// <c>b</c> to a dispatcher whose first send never completes.
/// </remarks>
public sealed class UnsentDispatchStrandingTests
{
    private readonly InMemoryFlowRunStore _store = new();
    private readonly InMemoryOutputsRepository _outputs = new();
    private readonly IStepDispatcher _dispatcher = Substitute.For<IStepDispatcher>();
    private readonly IStepExecutor _executor = Substitute.For<IStepExecutor>();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly IFlowDefinition _flow;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly List<(string StepKey, CancellationToken Token)> _sends = [];
    private readonly TaskCompletionSource<string?> _hungSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wires a flow <c>a → b</c> whose first send of <c>b</c> never returns.</summary>
    public UnsentDispatchStrandingTests()
    {
        _store.Clock = _clock;
        _flow = Substitute.For<IFlowDefinition>();
        _flow.Id.Returns(Guid.NewGuid());
        _flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "Work" },
                ["b"] = new StepMetadata
                {
                    Type = "Work",
                    RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
                }
            }
        });

        _executor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call => new ValueTask<IStepResult>(new StepResult
            {
                Key = call.ArgAt<IStepInstance>(2).Key,
                Status = StepStatus.Succeeded
            }));

        _dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                int count;
                lock (_sends)
                {
                    _sends.Add((call.ArgAt<IStepInstance>(2).Key, call.ArgAt<CancellationToken>(3)));
                    count = _sends.Count;
                }

                if (count == 1)
                {
                    _firstSendStarted.TrySetResult();
                    return new ValueTask<string?>(_hungSend.Task);
                }

                _secondSendStarted.TrySetResult();
                return new ValueTask<string?>("redelivered-" + count);
            });
    }

    [Fact]
    public async Task A_successor_dispatch_that_throws_OperationCanceled_releases_its_ledger_row()
    {
        // Arrange — no token reaches the dispatcher, so this cancellation is the adapter's own (an SDK or
        // HTTP timeout surfacing as TaskCanceledException). The filter used to exclude it from the release.
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new TaskCanceledException("send timed out inside the client"));
        await StartRunAsync();
        var engine = CreateEngine(dispatcher);

        // Act
        var thrown = await Record.ExceptionAsync(() => RunStepAsync(engine, "a"));

        // Assert — the failure still surfaces to the runtime, and the row no longer claims a queued attempt,
        // so startup recovery re-dispatches "b" instead of skipping it for ever.
        Assert.IsType<TaskCanceledException>(thrown);
        Assert.False(await _store.IsStepDispatchedAsync(_runId, "b"));
    }

    [Fact]
    public async Task A_send_that_never_returns_leaves_the_row_unannotated_and_startup_recovery_skips_it()
    {
        // Arrange — the root cause as it stood: the row exists, the runtime never got "b".
        await StartRunAsync();
        var engine = CreateEngine(_dispatcher);
        var stuck = RunStepAsync(engine, "a");
        await _firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        await CreateRecovery().StartAsync(CancellationToken.None);

        // Assert — recovery reads the ledger row as "queued in the runtime" and dispatches nothing.
        Assert.True(await _store.IsStepDispatchedAsync(_runId, "b"));
        Assert.Single(SendsSnapshot());
        await ReleaseHungSendAsync(stuck);
    }

    [Fact]
    public async Task The_unsent_dispatch_sweep_redelivers_the_stranded_step_with_no_token_and_annotates_it()
    {
        // Arrange
        await StartRunAsync();
        var engine = CreateEngine(_dispatcher);
        var stuck = RunStepAsync(engine, "a");
        await _firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(61));
        var sweep = CreateSweep(TimeSpan.FromSeconds(60));

        // Act
        var redelivered = await sweep.SweepOnceAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        // Assert — "b" went to the runtime a second time, with CancellationToken.None (the lease is a durable
        // write), and the annotation now marks the hand-off as completed, so nothing is leased again.
        await _secondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, redelivered);
        var sends = SendsSnapshot();
        Assert.Equal(["b", "b"], sends.Select(s => s.StepKey));
        Assert.False(sends[1].Token.CanBeCanceled);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(await _store.LeaseUnsentDispatchesAsync(TimeSpan.FromSeconds(60), 10));
        await ReleaseHungSendAsync(stuck);
    }

    [Fact]
    public async Task The_sweep_leaves_a_row_younger_than_the_timeout_alone()
    {
        // Arrange — the send is merely slow: 30 s in, against a 60 s timeout.
        await StartRunAsync();
        var engine = CreateEngine(_dispatcher);
        var stuck = RunStepAsync(engine, "a");
        await _firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        var redelivered = await CreateSweep(TimeSpan.FromSeconds(60)).SweepOnceAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        // Assert
        Assert.Equal(0, redelivered);
        Assert.Single(SendsSnapshot());
        await ReleaseHungSendAsync(stuck);
    }

    [Fact]
    public async Task A_runtime_that_returns_no_job_id_still_marks_the_hand_off_completed()
    {
        // Arrange — IStepDispatcher allows a null id. Unmarked, the row would read as a send that never
        // returned and be re-delivered once per timeout window until the step started.
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>((string?)null));
        await StartRunAsync();

        // Act
        await RunStepAsync(CreateEngine(dispatcher), "a");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var leased = await _store.LeaseUnsentDispatchesAsync(TimeSpan.FromSeconds(60), 10);

        // Assert
        Assert.True(await _store.IsStepDispatchedAsync(_runId, "b"));
        Assert.Empty(leased);
    }

    [Fact]
    public async Task A_runtime_token_cancelled_mid_step_does_not_reach_the_successor_dispatch()
    {
        // Arrange — guard for the "request/shutdown token after the durable write" class: the runtime
        // (Service Bus processor stopping, message lock lost) cancels its token while "a" runs. The
        // continuation's send of "b" happens after "b"'s ledger row is written, so it must not see it.
        using var runtimeToken = new CancellationTokenSource();
        _executor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call =>
            {
                runtimeToken.Cancel();
                return new ValueTask<IStepResult>(new StepResult { Key = call.ArgAt<IStepInstance>(2).Key, Status = StepStatus.Succeeded });
            });
        _hungSend.TrySetResult("job-b");
        await StartRunAsync();
        var engine = CreateEngine(_dispatcher);

        // Act
        await RunStepAsync(engine, "a", runtimeToken.Token);

        // Assert
        var send = Assert.Single(SendsSnapshot());
        Assert.Equal("b", send.StepKey);
        Assert.False(send.Token.CanBeCanceled);
    }

    private Task StartRunAsync() => _store.StartRunAsync(_flow.Id, "StrandFlow", _runId, "manual", null, null);

    private Task RunStepAsync(FlowOrchestratorEngine engine, string stepKey, CancellationToken ct = default) =>
        engine.RunStepAsync(new CoreExecutionContext { RunId = _runId }, _flow, new StepInstance(stepKey, "Work") { RunId = _runId }, ct).AsTask();

    private List<(string StepKey, CancellationToken Token)> SendsSnapshot()
    {
        lock (_sends)
        {
            return [.. _sends];
        }
    }

    private async Task ReleaseHungSendAsync(Task stuck)
    {
        _hungSend.TrySetResult("late-job");
        await stuck.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private FlowOrchestratorEngine CreateEngine(IStepDispatcher dispatcher) =>
        new(
            dispatcher,
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
            NoopFlowEventNotifier.Instance);

    private FlowRunRecoveryHostedService CreateRecovery() =>
        new(
            _store,
            [_store],
            new FlowRegistry([_flow]),
            new FlowGraphPlanner(),
            _dispatcher,
            _outputs,
            NullLogger<FlowRunRecoveryHostedService>.Instance);

    private FlowUnsentDispatchRecoveryHostedService CreateSweep(TimeSpan timeout)
    {
        var services = new ServiceCollection()
            .AddSingleton<IFlowRunStore>(_store)
            .AddSingleton(new FlowRegistry([_flow]))
            .AddSingleton(_dispatcher)
            .AddSingleton<IOutputsRepository>(_outputs)
            .BuildServiceProvider();
        return new FlowUnsentDispatchRecoveryHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FlowRunControlOptions { UnsentDispatchTimeout = timeout },
            NullLogger<FlowUnsentDispatchRecoveryHostedService>.Instance);
    }

    /// <summary>A clock that only moves when the test says so.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        /// <summary>Moves the clock forward.</summary>
        public void Advance(TimeSpan by) => _now += by;

        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
