using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;
using CoreExecutionContext = FlowOrchestrator.Core.Execution.ExecutionContext;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Stress regression for <see cref="WhenClauseEvaluator"/>: 64 tasks evaluate the same
/// When clause against the same trigger payload simultaneously. Verifies the evaluator
/// has no shared mutable state that produces inconsistent results under contention
/// (each call must converge on the same boolean outcome).
/// </summary>
public sealed class WhenConditionGatingConcurrencyTests
{
    [Fact]
    public async Task EvaluateAsync_64ParallelCallsForSameStep_AllProduceIdenticalResult()
    {
        // Arrange — When clause that simply reads a header value and compares it.
        var outputsRepo = Substitute.For<IOutputsRepository>();
        var runStore = Substitute.For<IFlowRunStore>();
        var evaluator = new WhenClauseEvaluator(outputsRepo, runStore);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());

        var metadata = new StepMetadata
        {
            Type = "Work",
            RunAfter =
            {
                ["upstream"] = new RunAfterCondition
                {
                    Statuses = [StepStatus.Succeeded],
                    When = "@triggerHeaders()['X-Approved'] == 'yes'"
                }
            }
        };

        var ctx = new CoreExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Approved"] = "yes"
            }
        };

        const int parallelism = 64;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act — race N evaluators on the same evaluator instance + context.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await evaluator.EvaluateAsync(ctx, flow, metadata);
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — clause was true for every caller (returns null when no clause is false).
        Assert.All(results, Assert.Null);
    }

    [Fact]
    public async Task EvaluateAsync_64ParallelCallsForFalseClause_AllAgreeOnFalseTrace()
    {
        // Arrange — clause that should evaluate to false; verifies all callers see the same trace.
        var outputsRepo = Substitute.For<IOutputsRepository>();
        var runStore = Substitute.For<IFlowRunStore>();
        var evaluator = new WhenClauseEvaluator(outputsRepo, runStore);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());

        var metadata = new StepMetadata
        {
            Type = "Work",
            RunAfter =
            {
                ["upstream"] = new RunAfterCondition
                {
                    Statuses = [StepStatus.Succeeded],
                    When = "@triggerHeaders()['X-Approved'] == 'yes'"
                }
            }
        };

        var ctx = new CoreExecutionContext
        {
            RunId = Guid.NewGuid(),
            TriggerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Approved"] = "no"
            }
        };

        const int parallelism = 64;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var tasks = Enumerable.Range(0, parallelism)
            .Select(async _ =>
            {
                await startGate.Task;
                return await evaluator.EvaluateAsync(ctx, flow, metadata);
            })
            .ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — every caller received the same false trace.
        Assert.All(results, trace =>
        {
            Assert.NotNull(trace);
            Assert.False(trace!.Result);
        });
    }
}
