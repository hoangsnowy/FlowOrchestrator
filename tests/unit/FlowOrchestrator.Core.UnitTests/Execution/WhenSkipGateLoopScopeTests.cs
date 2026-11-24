using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The When gate that startup recovery uses must evaluate a ForEach child's clause in its iteration scope, like
/// the graph continuation does: <c>@steps('work')</c> inside the loop body means this iteration's sibling.
/// </summary>
public sealed class WhenSkipGateLoopScopeTests
{
    [Fact]
    public async Task Loop_child_clause_referencing_its_sibling_is_evaluated_in_the_iteration_scope()
    {
        // Arrange — loop.0.work succeeded with ok=true; loop.0.check runs after it when ok == true.
        var runId = Guid.NewGuid();
        var check = new StepMetadata
        {
            Type = "Echo",
            RunAfter = new RunAfterCollection
            {
                ["work"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@steps('work').output.ok == true" }
            }
        };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Steps = new StepCollection
            {
                ["loop"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    ForEach = new List<object?> { "x" },
                    Steps = new StepCollection { ["work"] = new StepMetadata { Type = "Echo" }, ["check"] = check }
                }
            }
        });

        var outputs = Substitute.For<IOutputsRepository>();
        outputs.GetStepOutputAsync(runId, "loop.0.work")
            .Returns(new ValueTask<object?>(JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()));
        var runStore = Substitute.For<IFlowRunStore>();
        runStore.GetStepAsync(runId, "loop.0.work")
            .Returns(Task.FromResult<FlowStepRecord?>(new FlowStepRecord { RunId = runId, StepKey = "loop.0.work", Status = StepStatus.Succeeded.ToString() }));
        var runtimeStore = Substitute.For<IFlowRunRuntimeStore>();
        runtimeStore.TryClaimStepAsync(runId, Arg.Any<string>()).Returns(Task.FromResult(true));

        var gate = new WhenSkipGate(new WhenClauseEvaluator(outputs, runStore), runtimeStore, NullLogger.Instance);
        var ctx = new FlowOrchestrator.Core.Execution.ExecutionContext { RunId = runId };

        // Act
        var skipped = await gate.IsFalseAndSkippedAsync(ctx, flow, "loop.0.check", check);

        // Assert — the clause is true in this iteration, so recovery must dispatch the step, not skip it.
        Assert.False(skipped);
        await runtimeStore.DidNotReceiveWithAnyArgs().RecordSkippedStepAsync(default, default!, default!, default!, default);
    }
}
