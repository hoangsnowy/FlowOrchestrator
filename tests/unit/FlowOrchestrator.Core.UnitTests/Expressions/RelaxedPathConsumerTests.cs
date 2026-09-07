using System.Text.Json;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Execution.Internal;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Covers the consumers of the shared path walker that the original #177 fix left untested —
/// poll conditions, boolean <c>When</c> clauses, and ForEach sources — each of which reaches the
/// walker by a different route.
/// </summary>
public class RelaxedPathConsumerTests
{
    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    // ── Poll conditions ───────────────────────────────────────────────────────

    [Fact]
    public void PollCondition_PascalCasePath_MatchesCamelCaseResponse()
    {
        // Arrange — a C#-authored conditionPath against a camelCase API/stored response. This
        // carried its own case-sensitive copy of the walker, so it kept failing after #177: the
        // condition never matched and the step polled until its timeout.
        var payload = Json("{\"status\":{\"code\":\"DONE\"}}");

        // Act
        var matched = PollConditionEvaluator.IsMatched(payload, "Status.Code", "DONE");

        // Assert
        Assert.True(matched);
    }

    [Fact]
    public void PollCondition_PascalCasePresenceCheck_MatchesCamelCaseResponse()
    {
        // Arrange — the null-expectation form asserts presence rather than equality.
        var payload = Json("{\"deliveredAt\":\"2026-09-07T00:00:00Z\"}");

        // Act
        var matched = PollConditionEvaluator.IsMatched(payload, "DeliveredAt", expectedValue: null);

        // Assert
        Assert.True(matched);
    }

    [Fact]
    public void PollCondition_GenuinelyMissingPath_StillDoesNotMatch()
    {
        // Arrange — the relaxed lookup must not start matching absent properties.
        var payload = Json("{\"status\":{\"code\":\"DONE\"}}");

        // Act
        var matched = PollConditionEvaluator.IsMatched(payload, "Status.Warehouse", "DONE");

        // Assert
        Assert.False(matched);
    }

    [Fact]
    public void PollCondition_ArrayIndexSegment_StillResolves()
    {
        // Arrange — delegating to the shared walker must not lose numeric array indexing.
        var payload = Json("{\"events\":[{\"state\":\"NEW\"},{\"state\":\"DONE\"}]}");

        // Act
        var matched = PollConditionEvaluator.IsMatched(payload, "Events.1.State", "DONE");

        // Assert
        Assert.True(matched);
    }

    [Fact]
    public void PollCondition_EmptyPath_StillMeansThePayloadItself()
    {
        // Arrange
        var payload = Json("\"DONE\"");

        // Act
        var matched = PollConditionEvaluator.IsMatched(payload, conditionPath: null, expectedValue: "DONE");

        // Assert
        Assert.True(matched);
    }

    // ── Boolean / When clauses ────────────────────────────────────────────────

    private static async ValueTask<WhenEvaluationTrace?> EvaluateWhenAsync(string when, JsonElement trigger)
    {
        var evaluator = new WhenClauseEvaluator(
            Substitute.For<IOutputsRepository>(), Substitute.For<IFlowRunStore>());

        var metadata = new StepMetadata
        {
            Type = "Work",
            RunAfter = new RunAfterCollection { { "prev", new RunAfterCondition { Statuses = [StepStatus.Succeeded], When = when } } }
        };

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = new StepCollection { ["step"] = metadata } });

        var ctx = Substitute.For<IExecutionContext>();
        ctx.RunId.Returns(Guid.NewGuid());
        ctx.TriggerData.Returns(trigger);
        ctx.TriggerHeaders.Returns(default(IReadOnlyDictionary<string, string>?));

        return await evaluator.EvaluateAsync(ctx, flow, metadata);
    }

    [Fact]
    public async Task WhenClause_PascalCasePath_EvaluatesAgainstCamelCaseTriggerBody()
    {
        // Arrange — the same walker backs TriggerExpressionResolver, which feeds the boolean
        // evaluator, so RunAfter conditions and When clauses change outcome with it.
        var trigger = Json("{\"amount\":5000}");

        // Act — null trace means every clause evaluated true.
        var trace = await EvaluateWhenAsync("@triggerBody().Amount > 1000", trigger);

        // Assert
        Assert.Null(trace);
    }

    [Fact]
    public async Task WhenClause_PascalCasePathThatIsGenuinelyAbsent_StillResolvesToNull()
    {
        // Arrange — the relaxed lookup must not invent a value for a property that is absent in
        // every casing. A null operand under a relational operator is a hard error in the boolean
        // evaluator (pre-existing behaviour), so the throw is what proves the LHS stayed null.
        var trigger = Json("{\"amount\":5000}");

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await EvaluateWhenAsync("@triggerBody().Warehouse > 1000", trigger));

        // Assert
        Assert.Contains("null operand", ex.Message, StringComparison.Ordinal);
    }

    // ── ForEach sources ───────────────────────────────────────────────────────

    [Fact]
    public void ForEachSource_PascalCasePath_ResolvesAgainstCamelCaseTriggerBody()
    {
        // Arrange
        var trigger = Json("{\"steps\":[{\"code\":\"A\"},{\"code\":\"B\"}]}");

        // Act
        var items = ForEachSourceResolver.ToItemList(
            ForEachSourceResolver.Resolve("@triggerBody()?.Steps", trigger, null));

        // Assert
        Assert.Equal(2, items.Count);
    }
}
