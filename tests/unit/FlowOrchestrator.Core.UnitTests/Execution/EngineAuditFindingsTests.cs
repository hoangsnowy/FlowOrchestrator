using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Expressions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Failing reproductions for correctness defects found by the 2026-11-24 Core engine audit:
/// When-skip cascade gaps, When resolution inside loop scopes, expression-evaluator exceptions that
/// escape the authoring-error contract, mid-path null-conditional paths, and idempotency-key
/// registration that outlives a failed trigger.
/// </summary>
public sealed class EngineAuditFindingsTests
{
    private static IFlowDefinition FlowOf(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
            Steps = steps
        });
        return flow;
    }

    private static IStepResult Ok(string key, object? result = null) =>
        new StepResult { Key = key, Status = StepStatus.Succeeded, Result = result ?? new { ok = true } };

    private static IStepResult Park(string loopKey, int iterations, params string[] childKeys) =>
        new StepResult
        {
            Key = loopKey,
            Status = StepStatus.Running,
            Result = new { iterations },
            DispatchHint = new StepDispatchHint(
                [.. childKeys.Select(k => new StepDispatchRequest(k, "Echo", new Dictionary<string, object?>()))])
        };

    [Fact]
    public async Task WhenSkippedStep_DependentNeedingSucceeded_IsCascadeSkipped_AndSkipAcceptingGrandchildRuns()
    {
        // Arrange - a -> b (When false) -> c (needs b Succeeded) -> d (accepts c Succeeded or Skipped).
        var flow = FlowOf(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "Echo" },
            ["b"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["a"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@triggerBody().amount > 1000" }
                }
            },
            ["c"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] } },
            ["d"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["c"] = [StepStatus.Succeeded, StepStatus.Skipped] }
            }
        });
        var harness = new LoopBarrierEngineHarness(flow, key => Ok(key));
        var runId = await harness.TriggerAsync(JsonSerializer.SerializeToElement(new { amount = 5 }));

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.Equal("Skipped", await harness.StepStatusAsync(runId, "b"));
        Assert.True(
            await harness.StepStatusAsync(runId, "c") == "Skipped",
            $"c should be cascade-skipped after b was When-skipped; actual row: '{await harness.StepStatusAsync(runId, "c") ?? "<none>"}', run: '{await harness.RunStatusAsync(runId)}'.");
        Assert.Equal("Succeeded", await harness.StepStatusAsync(runId, "d"));
    }

    [Fact]
    public async Task LoopBody_WhenSkippedChildWithDependentSibling_LoopSettlesAndRunCompletes()
    {
        // Arrange - loop body: work -> check (When false) -> after (needs check Succeeded).
        var flow = FlowOf(new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = new List<object?> { "x" },
                Steps = new StepCollection
                {
                    ["work"] = new StepMetadata { Type = "Echo" },
                    ["check"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection
                        {
                            ["work"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@triggerBody().amount > 1000" }
                        }
                    },
                    ["after"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["check"] = [StepStatus.Succeeded] } }
                }
            },
            ["tail"] = new StepMetadata { Type = "Echo", RunAfter = new RunAfterCollection { ["loop"] = [StepStatus.Succeeded] } }
        });
        var harness = new LoopBarrierEngineHarness(flow, key => key == "loop" ? Park("loop", 1, "loop.0.work") : Ok(key));
        var runId = await harness.TriggerAsync(JsonSerializer.SerializeToElement(new { amount = 5 }));

        // Act
        await harness.DrainAsync();

        // Assert
        Assert.Equal("Skipped", await harness.StepStatusAsync(runId, "loop.0.check"));
        var runStatus = await harness.RunStatusAsync(runId);
        Assert.True(
            runStatus != "Running",
            $"Run stuck: status '{runStatus}', loop '{await harness.StepStatusAsync(runId, "loop")}', loop.0.after row '{await harness.StepStatusAsync(runId, "loop.0.after") ?? "<none>"}', queued: [{string.Join(", ", harness.PendingKeys)}].");
    }

    [Fact]
    public async Task LoopChild_WhenReferencingSiblingOutput_ResolvesInIterationScope()
    {
        // Arrange - body: work (outputs ok=true) -> check (When @steps('work').output.ok == true).
        var flow = FlowOf(new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = new List<object?> { "x" },
                Steps = new StepCollection
                {
                    ["work"] = new StepMetadata { Type = "Echo" },
                    ["check"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection
                        {
                            ["work"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@steps('work').output.ok == true" }
                        }
                    }
                }
            }
        });
        var harness = new LoopBarrierEngineHarness(flow, key => key == "loop" ? Park("loop", 1, "loop.0.work") : Ok(key));
        var runId = await harness.TriggerAsync();

        // Act
        await harness.DrainAsync();

        // Assert
        var check = await harness.StepRecordAsync(runId, "loop.0.check");
        Assert.True(
            check?.Status == "Succeeded",
            $"loop.0.check should run (work.output.ok is true); actual '{check?.Status}', reason: {check?.ErrorMessage}");
    }

    [Fact]
    public async Task Evaluate_MalformedNumberLiteral_ThrowsFlowExpressionException()
    {
        // Arrange
        var evaluator = new BooleanExpressionEvaluator();

        // Act
        var ex = await Record.ExceptionAsync(async () => await evaluator.EvaluateAsync("1.2.3 == 1", _ => new ValueTask<object?>((object?)null)));

        // Assert
        Assert.True(ex is FlowExpressionException, $"Expected FlowExpressionException, got {ex?.GetType().FullName}: {ex?.Message}");
    }

    [Fact]
    public async Task Evaluate_LhsNumberOutsideDecimalRange_DoesNotThrowOverflow()
    {
        // Arrange
        var evaluator = new BooleanExpressionEvaluator();
        var huge = JsonDocument.Parse("1e300").RootElement.Clone();

        // Act
        var ex = await Record.ExceptionAsync(async () => await evaluator.EvaluateAsync("@x > 5", _ => new ValueTask<object?>(huge)));

        // Assert
        Assert.True(ex is null or FlowExpressionException, $"Expected a result or FlowExpressionException, got {ex?.GetType().FullName}: {ex?.Message}");
    }

    [Fact]
    public async Task WhenClause_MalformedNumberLiteral_SkipsStepInsteadOfStrandingRun()
    {
        // Arrange - a -> b (When with a malformed literal). Contract: authoring error => skip with trace.
        var flow = FlowOf(new StepCollection
        {
            ["a"] = new StepMetadata { Type = "Echo" },
            ["b"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection
                {
                    ["a"] = new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = "@triggerBody().amount > 1.0.0" }
                }
            }
        });
        var harness = new LoopBarrierEngineHarness(flow, key => Ok(key));
        var runId = await harness.TriggerAsync(JsonSerializer.SerializeToElement(new { amount = 5 }));

        // Act
        var ex = await Record.ExceptionAsync(() => harness.DrainAsync());

        // Assert
        Assert.True(ex is null, $"RunStepAsync('a') threw {ex?.GetType().Name} after 'a' already succeeded; b row '{await harness.StepStatusAsync(runId, "b") ?? "<none>"}', run '{await harness.RunStatusAsync(runId)}'.");
        Assert.Equal("Skipped", await harness.StepStatusAsync(runId, "b"));
    }

    [Fact]
    public void TriggerBody_MidPathNullConditional_ResolvesNestedValue()
    {
        // Arrange
        var body = JsonSerializer.SerializeToElement(new { order = new { id = 42 } });

        // Act
        var matched = TriggerExpressionResolver.TryResolveTriggerBodyExpression("@triggerBody()?.order?.id", body, out var resolved);

        // Assert
        Assert.True(matched);
        Assert.True(
            resolved is JsonElement { ValueKind: JsonValueKind.Number } e && e.GetInt32() == 42,
            $"Expected 42, got '{resolved ?? "<null>"}'.");
    }

    [Fact]
    public async Task TriggerAsync_RetryWithSameIdempotencyKey_AfterDispatchFailure_StartsWork()
    {
        // Arrange - the first delivery fails at entry dispatch (transient broker error -> 5xx to caller).
        var flow = FlowOf(new StepCollection { ["a"] = new StepMetadata { Type = "Echo" } });
        var harness = new LoopBarrierEngineHarness(flow, key => Ok(key));
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "order-1" };
        harness.FailNextDispatch(new InvalidOperationException("broker unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.TriggerAsync(headers: headers));

        // Act - the caller retries the same delivery with the same key.
        var retryRunId = await harness.TriggerAsync(headers: headers);

        // Assert - the retry must lead to the entry step being dispatched.
        Assert.True(
            harness.Enqueued.Contains("a"),
            $"Retry with the same idempotency key was absorbed as a duplicate of run {retryRunId}, whose entry step was never dispatched (run status '{await harness.RunStatusAsync(retryRunId)}').");
    }
}
