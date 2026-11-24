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
}
