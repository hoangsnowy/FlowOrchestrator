using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Deterministic engine harness for loop-barrier tests. A recording <see cref="IStepDispatcher"/>
/// captures every dispatch into a pending list, and the drain helpers re-enter
/// <see cref="FlowOrchestratorEngine.RunStepAsync"/> inline — mirroring the InMemory runtime's
/// dispatch-then-execute loop with no scheduling delay, so tests can pick the exact interleaving
/// they want to exercise without depending on wall-clock timing.
/// </summary>
/// <remarks>
/// The store is the real <see cref="InMemoryFlowRunStore"/> (run store + runtime store + control
/// store) and the real <see cref="InMemoryOutputsRepository"/>, because the barrier reads the
/// iteration count back through <see cref="IOutputsRepository.GetStepOutputAsync"/> and the
/// completion gate reads step statuses back through <see cref="IFlowRunRuntimeStore"/>. Only the
/// step executor and the dispatcher are substituted.
/// </remarks>
internal sealed class LoopBarrierEngineHarness
{
    private readonly IFlowDefinition _flow;
    private readonly List<IStepInstance> _pending = [];
    private readonly List<string> _enqueued = [];

    /// <summary>Creates a harness bound to <paramref name="flow"/>.</summary>
    /// <param name="flow">Flow definition executed by the harness.</param>
    /// <param name="resultForStep">
    /// Maps a runtime step key to the result the substituted <see cref="IStepExecutor"/> returns.
    /// </param>
    public LoopBarrierEngineHarness(IFlowDefinition flow, Func<string, IStepResult> resultForStep)
    {
        _flow = flow;
        Store = new InMemoryFlowRunStore();

        var dispatcher = Substitute.For<IStepDispatcher>();

        ValueTask<string?> Capture(NSubstitute.Core.CallInfo call)
        {
            var step = call.Arg<IStepInstance>()!;
            lock (_pending)
            {
                _enqueued.Add(step.Key);
                _pending.Add(step);
            }
            return new ValueTask<string?>("job-" + step.Key);
        }

        dispatcher.EnqueueStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>(), Arg.Any<CancellationToken>())
            .Returns(Capture);

        dispatcher.ScheduleStepAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(),
                Arg.Any<IStepInstance>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Capture);

        var stepExecutor = Substitute.For<IStepExecutor>();
        stepExecutor.ExecuteAsync(
                Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Any<IStepInstance>())
            .Returns(call => new ValueTask<IStepResult>(resultForStep(call.Arg<IStepInstance>()!.Key)));

        var flowRepo = Substitute.For<IFlowRepository>();
        flowRepo.GetAllFlowsAsync().Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));
        FlowRepository = flowRepo;

        Engine = new FlowOrchestratorEngine(
            dispatcher,
            Substitute.For<IFlowExecutor>(),
            new FlowGraphPlanner(),
            stepExecutor,
            Substitute.For<IFlowStore>(),
            Store,
            new InMemoryOutputsRepository(),
            Substitute.For<IExecutionContextAccessor>(),
            flowRepo,
            [Store],
            [Store],
            new FlowRunControlOptions(),
            new FlowObservabilityOptions { EnableEventPersistence = false, EnableOpenTelemetry = false },
            new FlowOrchestratorTelemetry(),
            Substitute.For<ILogger<FlowOrchestratorEngine>>());
    }

    /// <summary>Engine under test.</summary>
    public FlowOrchestratorEngine Engine { get; }

    /// <summary>Real in-memory store backing run, runtime, and control state.</summary>
    public InMemoryFlowRunStore Store { get; }

    /// <summary>Substituted flow repository, pre-seeded with the harness flow.</summary>
    public IFlowRepository FlowRepository { get; }

    /// <summary>Every step key the dispatcher was asked to enqueue, in call order.</summary>
    public IReadOnlyList<string> Enqueued
    {
        get
        {
            lock (_pending)
            {
                return [.. _enqueued];
            }
        }
    }

    /// <summary>Step keys captured by the dispatcher and not yet executed.</summary>
    public IReadOnlyList<string> PendingKeys
    {
        get
        {
            lock (_pending)
            {
                return _pending.ConvertAll(s => s.Key);
            }
        }
    }

    /// <summary>Triggers the flow and returns the run id the engine settled on.</summary>
    /// <param name="body">Trigger payload.</param>
    /// <param name="headers">
    /// Trigger headers; supply an idempotency key here to exercise the duplicate-trigger path.
    /// </param>
    public async Task<Guid> TriggerAsync(object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var ctx = new TriggerContext
        {
            RunId = Guid.NewGuid(),
            Flow = _flow,
            Trigger = new Trigger("manual", "Manual", body, headers: headers)
        };

        await Engine.TriggerAsync(ctx);
        return ctx.RunId;
    }

    /// <summary>Executes the oldest queued step.</summary>
    public Task RunNextAsync() => RunAtAsync(0);

    /// <summary>Executes the queued step with the given key, failing the test when it is absent.</summary>
    public Task RunKeyAsync(string stepKey)
    {
        int index;
        lock (_pending)
        {
            index = _pending.FindIndex(s => string.Equals(s.Key, stepKey, StringComparison.Ordinal));
        }

        Assert.True(index >= 0, $"Step '{stepKey}' is not queued. Queued: {string.Join(", ", PendingKeys)}.");
        return RunAtAsync(index);
    }

    /// <summary>Executes every queued step, including work queued while draining.</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            lock (_pending)
            {
                if (_pending.Count == 0)
                {
                    return;
                }
            }

            await RunAtAsync(0);
        }
    }

    /// <summary>Executes two queued steps concurrently, so their continuations genuinely overlap.</summary>
    public Task RunConcurrentlyAsync(string firstStepKey, string secondStepKey)
    {
        var first = Take(firstStepKey);
        var second = Take(secondStepKey);
        return Task.WhenAll(Invoke(first), Invoke(second));

        IStepInstance Take(string key)
        {
            lock (_pending)
            {
                var index = _pending.FindIndex(s => string.Equals(s.Key, key, StringComparison.Ordinal));
                Assert.True(index >= 0, $"Step '{key}' is not queued.");
                var step = _pending[index];
                _pending.RemoveAt(index);
                return step;
            }
        }

        Task Invoke(IStepInstance step) => Task.Run(() =>
            Engine.RunStepAsync(new CoreExecutionContext { RunId = step.RunId }, _flow, step).AsTask());
    }

    /// <summary>
    /// Re-enters the engine for a step that is not (or no longer) in the pending queue, modelling
    /// an at-least-once redelivery of a message the runtime already delivered once.
    /// </summary>
    /// <param name="runId">Run the redelivered message belongs to.</param>
    /// <param name="stepKey">Runtime key carried by the redelivered message.</param>
    /// <param name="stepType">Handler type carried by the redelivered message.</param>
    public Task RedeliverAsync(Guid runId, string stepKey, string stepType)
    {
        var step = new StepInstance(stepKey, stepType)
        {
            RunId = runId,
            ScheduledTime = DateTimeOffset.UtcNow,
            Inputs = new Dictionary<string, object?>()
        };

        return Engine.RunStepAsync(new CoreExecutionContext { RunId = runId }, _flow, step).AsTask();
    }

    /// <summary>Current persisted run status.</summary>
    public async Task<string> RunStatusAsync(Guid runId) =>
        (await Store.GetRunDetailAsync(runId))!.Status;

    /// <summary>Current persisted status of a step, or <see langword="null"/> when it has no row yet.</summary>
    public async Task<string?> StepStatusAsync(Guid runId, string stepKey)
    {
        var detail = await Store.GetRunDetailAsync(runId);
        return detail?.Steps?.FirstOrDefault(s => s.StepKey == stepKey)?.Status;
    }

    private async Task RunAtAsync(int index)
    {
        IStepInstance step;
        lock (_pending)
        {
            step = _pending[index];
            _pending.RemoveAt(index);
        }

        await Engine.RunStepAsync(new CoreExecutionContext { RunId = step.RunId }, _flow, step);
    }
}
