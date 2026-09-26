using System.Diagnostics.Metrics;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>
/// End-to-end regression coverage for issue #190, race 1: a signal delivered while the parking
/// invocation still holds its execution claim.
/// </summary>
/// <remarks>
/// <para>
/// <c>FlowSignalDispatcher</c> sees the claim held and sends a nudge 500 ms later, on the assumption
/// that the claim will be gone by then. These tests make that assumption false on purpose: the waiter
/// registration — which runs inside the parking invocation, under its claim — delivers the signal and
/// then refuses to return until the nudge has actually lost <c>TryClaimStepAsync</c>. Up to v1.32 that
/// stranded the step until its 24-hour safety net, so the run never reached a terminal state inside
/// the trigger budget. The engine now re-reads the waiter after releasing the claim and resumes the
/// step itself.
/// </para>
/// <para>
/// Everything here is real: InMemory storage, the InMemory channel runtime, the built-in
/// <c>WaitForSignal</c> handler and the real <see cref="IFlowSignalDispatcher"/>. The only seam is a
/// delegating <see cref="IFlowSignalStore"/>, and the only wait is on a logical event (the
/// <c>flow_step_claim_lost</c> measurement), never on wall-clock sleeps.
/// </para>
/// </remarks>
public sealed class SignalDeliveredWhileClaimedTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Signal_delivered_while_the_claim_is_held_still_resumes_the_step()
    {
        // Arrange
        var store = new RacingSignalStore(holdUntilNudgeLosesClaim: true);
        using var claimLost = new ClaimLostListener(ApprovalFlow.StepKey);
        store.ClaimLost = claimLost.FirstLoss;
        await using var host = await FlowTestHost.For<ApprovalFlow>()
            .WithService<IFlowSignalStore>(store)
            .WithService<IStepDispatcher, ParallelWorkerStepDispatcher>()
            .BuildAsync();
        store.Dispatcher = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act
        var result = await host.TriggerAsync(timeout: Budget);

        // Assert — the race really happened (otherwise this test proves nothing) …
        Assert.True(claimLost.FirstLoss.Task.IsCompletedSuccessfully, "the resume nudge never lost the claim; the race was not reproduced");
        Assert.True(store.DeliveredWhileClaimed, "the signal was not delivered inside the parking invocation");

        // … and the step still resumed well inside the budget instead of waiting 24 hours.
        Assert.False(result.TimedOut, "run stranded: the step never resumed after its nudge lost the claim");
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps[ApprovalFlow.StepKey].Status);
        Assert.True(result.Steps[ApprovalFlow.StepKey].Output.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task Parked_step_timeline_never_reports_completion_before_it_completes()
    {
        // Arrange — same race; this time assert on the persisted event timeline.
        var store = new RacingSignalStore(holdUntilNudgeLosesClaim: true);
        using var claimLost = new ClaimLostListener(ApprovalFlow.StepKey);
        store.ClaimLost = claimLost.FirstLoss;
        await using var host = await FlowTestHost.For<ApprovalFlow>()
            .WithService<IFlowSignalStore>(store)
            .WithService<IStepDispatcher, ParallelWorkerStepDispatcher>()
            .BuildAsync();
        store.Dispatcher = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act
        var result = await host.TriggerAsync(timeout: Budget);

        // Assert — no spurious "completed with status Pending", exactly one real completion, and the
        // park is recorded before it.
        var stepEvents = result.Events.Where(e => e.StepKey == ApprovalFlow.StepKey).OrderBy(e => e.Sequence).ToList();
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.DoesNotContain(stepEvents, e => e.Message?.Contains("with status Pending", StringComparison.Ordinal) == true);
        var completed = Assert.Single(stepEvents, e => e.Type == "step.completed");
        var pending = stepEvents.First(e => e.Type == "step.pending");
        Assert.True(pending.Sequence < completed.Sequence, string.Join(" → ", stepEvents.Select(e => e.Type)));
    }

    [Fact]
    public async Task Concurrent_runs_whose_signals_land_inside_the_parking_invocation_all_complete()
    {
        // Arrange — 32 runs at once, every signal delivered while its step still holds the claim. Both
        // the dispatcher's delayed nudge and the engine's post-release re-check fire for each run; the
        // claim must admit exactly one and every run must finish.
        const int runs = 32;
        var store = new RacingSignalStore(holdUntilNudgeLosesClaim: false);
        await using var host = await FlowTestHost.For<ApprovalFlow>()
            .WithService<IFlowSignalStore>(store)
            .WithService<IStepDispatcher, ParallelWorkerStepDispatcher>()
            .BuildAsync();
        store.Dispatcher = host.Services.GetRequiredService<IFlowSignalDispatcher>();

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, runs).Select(_ => host.TriggerAsync(timeout: Budget)));

        // Assert
        Assert.All(results, r =>
        {
            Assert.False(r.TimedOut, $"run {r.RunId} stranded");
            Assert.Equal(RunStatus.Succeeded, r.Status);
            Assert.Single(r.Events, e => e.StepKey == ApprovalFlow.StepKey && e.Type == "step.completed");
        });
        Assert.Equal(runs, store.Deliveries);
    }

    /// <summary>One-step flow parked on the built-in <c>WaitForSignal</c> with no timeout (24-hour safety net).</summary>
    public sealed class ApprovalFlow : IFlowDefinition
    {
        /// <summary>The single parked step.</summary>
        public const string StepKey = "approve";

        /// <inheritdoc/>
        public Guid Id { get; } = new("cccccccc-1900-1900-1900-cccccccccccc");

        /// <inheritdoc/>
        public string Version => "1.0";

        /// <inheritdoc/>
        public FlowManifest Manifest { get; set; } = new()
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                [StepKey] = new StepMetadata
                {
                    Type = "WaitForSignal",
                    Inputs = new Dictionary<string, object?> { ["signalName"] = "approval" }
                }
            }
        };
    }

    /// <summary>
    /// Delegating signal store that delivers the signal from inside <c>RegisterWaiterAsync</c> — i.e.
    /// while the parking invocation holds its claim — and can hold that invocation until the resume
    /// nudge has lost the claim.
    /// </summary>
    private sealed class RacingSignalStore(bool holdUntilNudgeLosesClaim) : IFlowSignalStore
    {
        private readonly InMemoryFlowSignalStore _inner = new();
        private int _deliveries;

        public IFlowSignalDispatcher? Dispatcher { get; set; }

        public TaskCompletionSource? ClaimLost { get; set; }

        public bool DeliveredWhileClaimed { get; private set; }

        public int Deliveries => Volatile.Read(ref _deliveries);

        public async ValueTask RegisterWaiterAsync(Guid runId, string stepKey, string signalName, DateTimeOffset? expiresAt, CancellationToken ct = default)
        {
            await _inner.RegisterWaiterAsync(runId, stepKey, signalName, expiresAt, ct);

            var delivery = await Dispatcher!.DispatchAsync(runId, signalName, "{\"approved\":true}", CancellationToken.None);
            if (delivery.Status != SignalDeliveryStatus.Delivered)
            {
                return;
            }

            Interlocked.Increment(ref _deliveries);
            DeliveredWhileClaimed = true;
            if (holdUntilNudgeLosesClaim)
            {
                // Keep the claim until the nudge has come and lost — the exact v1.32 stranding sequence.
                await ClaimLost!.Task.WaitAsync(Budget, CancellationToken.None);
            }
        }

        public ValueTask<SignalDeliveryResult> DeliverSignalAsync(Guid runId, string signalName, string payloadJson, CancellationToken ct = default) =>
            _inner.DeliverSignalAsync(runId, signalName, payloadJson, ct);

        public ValueTask<FlowSignalWaiter?> GetWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default) =>
            _inner.GetWaiterAsync(runId, stepKey, ct);

        public ValueTask RemoveWaiterAsync(Guid runId, string stepKey, CancellationToken ct = default) =>
            _inner.RemoveWaiterAsync(runId, stepKey, ct);
    }

    /// <summary>
    /// Multi-worker runtime stand-in: every dispatched attempt runs on its own task in its own DI scope,
    /// the way Hangfire workers or Service Bus processors pick up jobs.
    /// </summary>
    /// <remarks>
    /// Needed because the InMemory runtime drains its channel with one consumer by default, and with
    /// one consumer race 1 cannot happen at all: the nudge simply waits in the channel until the
    /// parking invocation has finished and released the claim. The race only exists where a second
    /// worker can pick the nudge up while the first still holds the claim.
    /// </remarks>
    private sealed class ParallelWorkerStepDispatcher(IServiceScopeFactory scopeFactory) : IStepDispatcher, IDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private int _jobs;

        public ValueTask<string?> EnqueueStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, CancellationToken ct = default) =>
            Start(context, flow, step, TimeSpan.Zero);

        public ValueTask<string?> ScheduleStepAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step, TimeSpan delay, CancellationToken ct = default) =>
            Start(context, flow, step, delay);

        public void Dispose()
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }

        private ValueTask<string?> Start(IExecutionContext context, IFlowDefinition flow, IStepInstance step, TimeSpan delay)
        {
            var jobId = $"job-{Interlocked.Increment(ref _jobs)}";
            var token = _shutdown.Token;

            // A fresh context per attempt, as a real runtime rebuilds it from the job payload.
            var jobContext = new Core.Execution.ExecutionContext
            {
                RunId = context.RunId,
                PrincipalId = context.PrincipalId,
                TriggerData = context.TriggerData,
                TriggerHeaders = context.TriggerHeaders,
                JobId = jobId,
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, token);
                    }

                    await using var scope = scopeFactory.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
                    await engine.RunStepAsync(jobContext, flow, step, CancellationToken.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Host disposed with a safety-net attempt still parked — expected.
                }
            }, CancellationToken.None);

            return ValueTask.FromResult<string?>(jobId);
        }
    }

    /// <summary>Completes <see cref="FirstLoss"/> on the first <c>flow_step_claim_lost</c> measurement for the step.</summary>
    private sealed class ClaimLostListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ClaimLostListener(string stepKey)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FlowOrchestratorTelemetry.SourceName && instrument.Name == "flow_step_claim_lost")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "step_key" && (tag.Value as string) == stepKey)
                    {
                        FirstLoss.TrySetResult();
                    }
                }
            });
            _listener.Start();
        }

        public TaskCompletionSource FirstLoss { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => _listener.Dispose();
    }
}
