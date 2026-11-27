using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Covers a manual retry of a <c>WaitForSignal</c> step that failed because its <c>timeoutSeconds</c>
/// deadline elapsed.
/// </summary>
/// <remarks>
/// The handler deliberately leaves the expired waiter row in place as an idempotency tombstone, and
/// <see cref="FlowOrchestratorEngine.RetryStepAsync"/> resets the step row, claim and ledger but never the
/// waiter. The retried attempt therefore lands in the handler's "waiter expired" branch on its first
/// invocation and fails again with the same "not received within Ns" reason without ever re-arming the
/// wait: a timed-out signal wait can never be retried, no matter how often the operator presses Retry.
/// </remarks>
public sealed class WaitForSignalRetryAfterTimeoutTests
{
    private const string StepKey = "approve";

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TypedSignalStep(IStepInstance inner, WaitForSignalInput inputs) : IStepInstance<WaitForSignalInput>
    {
        public Guid RunId { get => inner.RunId; set => inner.RunId = value; }
        public string? PrincipalId { get => inner.PrincipalId; set => inner.PrincipalId = value; }
        public object? TriggerData { get => inner.TriggerData; set => inner.TriggerData = value; }
        public IReadOnlyDictionary<string, string>? TriggerHeaders { get => inner.TriggerHeaders; set => inner.TriggerHeaders = value; }
        public string? JobId { get => inner.JobId; set => inner.JobId = value; }
        public DateTimeOffset ScheduledTime { get => inner.ScheduledTime; set => inner.ScheduledTime = value; }
        public string Type { get => inner.Type; set => inner.Type = value; }
        public string Key => inner.Key;
        public WaitForSignalInput Inputs { get; set; } = inputs;
        public int Index { get => inner.Index; set => inner.Index = value; }
    }

    [Fact]
    public async Task RetryStepAsync_OnWaitForSignalThatTimedOut_ReArmsTheWaitInsteadOfFailingImmediately()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var signalStore = new InMemoryFlowSignalStore();
        var handler = new WaitForSignalHandler(signalStore, clock);
        var runStore = new InMemoryFlowRunStore();

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                [StepKey] = new StepMetadata
                {
                    Type = WaitForSignalHandler.StepTypeName,
                    Inputs = new Dictionary<string, object?> { ["signalName"] = "approval", ["timeoutSeconds"] = 5 }
                }
            }
        });

        var executor = Substitute.For<IStepExecutor>();
        executor.ExecuteAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call =>
            {
                var step = call.Arg<IStepInstance>()!;
                var typed = new TypedSignalStep(step, new WaitForSignalInput { SignalName = "approval", TimeoutSeconds = 5 });
                var result = handler.ExecuteAsync(call.Arg<IExecutionContext>()!, call.Arg<IFlowDefinition>()!, typed)
                    .AsTask().GetAwaiter().GetResult();
                return new ValueTask<IStepResult>((IStepResult)result!);
            });

        var queued = new Queue<IStepInstance>();
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(call => { queued.Enqueue(call.Arg<IStepInstance>()!); return new ValueTask<string?>("job"); });
        dispatcher.ScheduleStepAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(call => { queued.Enqueue(call.Arg<IStepInstance>()!); return new ValueTask<string?>("job"); });

        var engine = new FlowOrchestratorEngine(
            dispatcher,
            new FlowGraphPlanner(),
            executor,
            Substitute.For<IFlowStore>(),
            runStore,
            Substitute.For<IOutputsRepository>(),
            Substitute.For<IExecutionContextAccessor>(),
            new FlowRegistry([flow]),
            [runStore],
            [runStore],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>(),
            NoopFlowEventNotifier.Instance,
            signalStore);

        await runStore.StartRunAsync(flowId, "SignalFlow", runId, "manual", null, null);
        await runStore.ConfigureRunAsync(runId, flowId, "manual", null, null);

        // Attempt 1 parks; the deadline then passes with no signal and the safety-net attempt fails the step.
        await engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, flow,
            new StepInstance(StepKey, WaitForSignalHandler.StepTypeName) { RunId = runId });
        Assert.Equal(StepStatus.Pending, (await runStore.GetStepStatusesAsync(runId))[StepKey]);
        clock.Now += TimeSpan.FromSeconds(10);
        await engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, flow, queued.Dequeue());
        Assert.Equal(StepStatus.Failed, (await runStore.GetStepStatusesAsync(runId))[StepKey]);

        // Act — the operator retries the timed-out wait, and the runtime runs the attempt it dispatched.
        var retry = Assert.IsType<StepRetryResult>(await engine.RetryStepAsync(flowId, runId, StepKey));
        Assert.True(retry.Accepted);
        await engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, flow, queued.Dequeue());

        // Assert — the retried step waits for a signal again rather than re-failing on the stale waiter.
        var step = await runStore.GetStepAsync(runId, StepKey);
        Assert.True(
            step!.Status == nameof(StepStatus.Pending),
            $"Retried WaitForSignal step ended '{step.Status}' ({step.ErrorMessage}) instead of re-arming its wait.");
    }
}
