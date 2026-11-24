using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Hosting;

/// <summary>
/// <see cref="FlowRunRecoveryHostedService"/> must honour a ready step's <c>When</c> clause exactly as the
/// engine's graph continuation does.
/// </summary>
/// <remarks>
/// <c>When</c> is evaluated only at SCHEDULE time — in <c>TriggerAsync</c> for entry steps and in the graph
/// continuation for successors (<c>TryEvaluateWhenAndSkipAsync</c>). <c>RunStepAsync</c> never evaluates it.
/// Recovery classifies a step Ready from statuses alone and dispatches it straight to the runtime, so when the
/// host dies between a parent's completion write and its continuation, the restarted host executes a child
/// whose <c>When</c> is false: a "refund only when amount &gt; 100" step runs for an amount of 5.
/// </remarks>
public sealed class FlowRunRecoveryWhenClauseTests
{
    [Fact]
    public async Task StartAsync_ReadyStepWhoseWhenIsFalse_IsSkippedNotDispatched()
    {
        // Arrange — "check" succeeded with amount 5; the host died before the continuation evaluated
        // "refund"'s When clause, so "refund" has neither a status row nor a ledger row.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["check"] = new StepMetadata { Type = "Check" },
                ["refund"] = new StepMetadata
                {
                    Type = "Refund",
                    RunAfter = new RunAfterCollection
                    {
                        { "check", new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@steps('check').output.amount > 100" } }
                    }
                }
            }
        });

        var store = new InMemoryFlowRunStore();
        var outputs = new InMemoryOutputsRepository(store);
        await store.StartRunAsync(flowId, "RefundFlow", runId, "manual", null, null);
        await store.TryClaimStepAsync(runId, "check");
        await store.RecordStepStartAsync(runId, "check", "Check", null, null);
        await outputs.SaveStepOutputAsync(
            new CoreExecutionContext { RunId = runId }, flow, new StepInstance("check", "Check") { RunId = runId },
            new StepResult { Key = "check", Status = StepStatus.Succeeded, Result = new { amount = 5 } });
        await store.RecordStepCompleteAsync(runId, "check", nameof(StepStatus.Succeeded), "{\"amount\":5}", null);

        var dispatcher = Substitute.For<IStepDispatcher>();
        var sut = new FlowRunRecoveryHostedService(
            store,
            [store],
            new FlowRegistry([flow]),
            new FlowGraphPlanner(),
            dispatcher,
            outputs,
            NullLogger<FlowRunRecoveryHostedService>.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert — the false When must not be bypassed by recovery.
        var dispatchedKeys = dispatcher.ReceivedCalls()
            .Select(c => c.GetArguments().OfType<IStepInstance>().FirstOrDefault()?.Key)
            .Where(k => k is not null)
            .ToList();
        Assert.True(!dispatchedKeys.Contains("refund"),
            "Recovery dispatched 'refund' although its When clause (@steps('check').output.amount > 100) is false for amount 5.");
    }

    /// <summary>Builds check → refund(When amount &gt; 100) → notify(RunAfter refund Succeeded), with "check" done.</summary>
    private static async Task<(IFlowDefinition Flow, InMemoryFlowRunStore Store, InMemoryOutputsRepository Outputs, Guid RunId)>
        ArrangeRefundRunAsync(int amount, bool withNotify)
    {
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var steps = new StepCollection
        {
            ["check"] = new StepMetadata { Type = "Check" },
            ["refund"] = new StepMetadata
            {
                Type = "Refund",
                RunAfter = new RunAfterCollection
                {
                    { "check", new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@steps('check').output.amount > 100" } }
                }
            }
        };
        if (withNotify)
        {
            steps["notify"] = new StepMetadata
            {
                Type = "Notify",
                RunAfter = new RunAfterCollection { { "refund", new RunAfterCondition { Statuses = [StepStatus.Succeeded] } } }
            };
        }

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest { Steps = steps });

        var store = new InMemoryFlowRunStore();
        var outputs = new InMemoryOutputsRepository(store);
        await store.StartRunAsync(flowId, "RefundFlow", runId, "manual", null, null);
        await store.TryClaimStepAsync(runId, "check");
        await store.RecordStepStartAsync(runId, "check", "Check", null, null);
        await outputs.SaveStepOutputAsync(
            new CoreExecutionContext { RunId = runId }, flow, new StepInstance("check", "Check") { RunId = runId },
            new StepResult { Key = "check", Status = StepStatus.Succeeded, Result = new { amount } });
        await store.RecordStepCompleteAsync(runId, "check", nameof(StepStatus.Succeeded), $"{{\"amount\":{amount}}}", null);
        return (flow, store, outputs, runId);
    }

    private static FlowRunRecoveryHostedService CreateSut(
        IFlowDefinition flow, InMemoryFlowRunStore store, InMemoryOutputsRepository outputs, IStepDispatcher dispatcher) =>
        new(store, [store], new FlowRegistry([flow]), new FlowGraphPlanner(), dispatcher, outputs,
            NullLogger<FlowRunRecoveryHostedService>.Instance);

    private static List<string?> DispatchedKeys(IStepDispatcher dispatcher) => dispatcher.ReceivedCalls()
        .Select(c => c.GetArguments().OfType<IStepInstance>().FirstOrDefault()?.Key)
        .Where(k => k is not null)
        .ToList();

    [Fact]
    public async Task StartAsync_ReadyStepWhoseWhenIsFalse_IsRecordedSkippedWithTheEngineReason()
    {
        // Arrange
        var (flow, store, outputs, runId) = await ArrangeRefundRunAsync(amount: 5, withNotify: false);
        var sut = CreateSut(flow, store, outputs, Substitute.For<IStepDispatcher>());

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert — the skip row is the one the continuation would have written (status, reason, trace),
        // and the run, now fully terminal, is closed.
        var statuses = await store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Skipped, statuses["refund"]);
        var detail = await store.GetRunDetailAsync(runId);
        Assert.NotNull(detail);
        var refund = Assert.Single(detail!.Steps!, s => s.StepKey == "refund");
        Assert.StartsWith("When clause '@steps('check').output.amount > 100' evaluated to false", refund.ErrorMessage);
        Assert.NotNull(refund.EvaluationTraceJson);
        Assert.NotEqual("Running", detail.Status);
    }

    [Fact]
    public async Task StartAsync_ReadyStepWhoseWhenIsTrue_IsDispatched()
    {
        // Arrange
        var (flow, store, outputs, _) = await ArrangeRefundRunAsync(amount: 500, withNotify: false);
        var dispatcher = Substitute.For<IStepDispatcher>();
        var sut = CreateSut(flow, store, outputs, dispatcher);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert
        Assert.Contains("refund", DispatchedKeys(dispatcher));
    }

    [Fact]
    public async Task StartAsync_WhenSkipBlocksADependent_RecordsTheDependentSkippedAndClosesTheRun()
    {
        // Arrange — refund is When-skipped; notify requires refund Succeeded, so it can never run.
        var (flow, store, outputs, runId) = await ArrangeRefundRunAsync(amount: 5, withNotify: true);
        var dispatcher = Substitute.For<IStepDispatcher>();
        var sut = CreateSut(flow, store, outputs, dispatcher);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Assert — the cascade the continuation would have run, then the zombie close.
        Assert.Empty(DispatchedKeys(dispatcher));
        var statuses = await store.GetStepStatusesAsync(runId);
        Assert.Equal(StepStatus.Skipped, statuses["refund"]);
        Assert.Equal(StepStatus.Skipped, statuses["notify"]);
        var detail = await store.GetRunDetailAsync(runId);
        Assert.NotEqual("Running", detail!.Status);
    }

    [Fact]
    public async Task StartAsync_OneDispatchThrows_StillRecoversTheRunsOtherReadySteps()
    {
        // Arrange — two independent entry steps never reached the runtime; dispatching the first throws.
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["a"] = new StepMetadata { Type = "A" },
                ["b"] = new StepMetadata { Type = "B" },
            }
        });
        var store = new InMemoryFlowRunStore();
        var outputs = new InMemoryOutputsRepository(store);
        await store.StartRunAsync(flowId, "TwoEntries", runId, "manual", null, null);

        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.EnqueueStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Is<IStepInstance>(s => s.Key == "a"), Arg.Any<CancellationToken>())
            .Returns<ValueTask<string?>>(_ => throw new InvalidOperationException("broker unavailable"));
        dispatcher.EnqueueStepAsync(Arg.Any<IExecutionContext>(), Arg.Any<IFlowDefinition>(), Arg.Is<IStepInstance>(s => s.Key == "b"), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string?>("job-b"));
        var sut = CreateSut(flow, store, outputs, dispatcher);

        // Act
        var failure = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));

        // Assert — "b" still reached the runtime; "a"'s ledger row was released for the next pass; the
        // run was not closed as a zombie.
        Assert.Null(failure);
        Assert.Contains("b", DispatchedKeys(dispatcher));
        var ledger = await store.GetDispatchedStepKeysAsync(runId);
        Assert.DoesNotContain("a", ledger);
        Assert.Contains("b", ledger);
        var detail = await store.GetRunDetailAsync(runId);
        Assert.Equal("Running", detail!.Status);
    }
}
