using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Storage-contract audit: behaviours where the in-memory stores diverge from the SQL Server and
/// PostgreSQL providers. Each test states the parity expectation; a failure is the defect.
/// </summary>
public sealed class InMemoryStoreDivergenceAuditTests
{
    private static IFlowDefinition CreateFlow()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest());
        return flow;
    }

    [Fact]
    public async Task GetTriggerHeaders_is_case_insensitive_even_when_the_caller_passed_an_ordinal_dictionary()
    {
        // Arrange — a programmatic trigger with a default (ordinal) dictionary. Sql/PostgreSqlOutputsRepository
        // rehydrate headers into an OrdinalIgnoreCase dictionary, so a later step's
        // @triggerHeaders()['x-request-id'] resolves there; InMemory hands back the caller's dictionary.
        var sut = new InMemoryOutputsRepository();
        var flow = CreateFlow();
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "req-1" };
        var trigger = new Trigger("manual", "Manual", null, headers);
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };
        await sut.SaveTriggerHeadersAsync(ctx, flow, trigger);

        // Act
        var stored = await sut.GetTriggerHeadersAsync(ctx.RunId);

        // Assert
        Assert.NotNull(stored);
        Assert.True(stored!.TryGetValue("x-request-id", out var value));
        Assert.Equal("req-1", value);
    }

    [Fact]
    public async Task GetTriggerHeaders_is_a_snapshot_not_the_callers_live_dictionary()
    {
        // Arrange — the SQL stores serialise the headers at save time; InMemory keeps the reference,
        // so a caller that reuses its dictionary rewrites the headers of a run already started.
        var sut = new InMemoryOutputsRepository();
        var flow = CreateFlow();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "a" };
        var trigger = new Trigger("manual", "Manual", null, headers);
        var ctx = new TriggerContext { RunId = Guid.NewGuid(), Flow = flow, Trigger = trigger };
        await sut.SaveTriggerHeadersAsync(ctx, flow, trigger);

        // Act
        headers["X-Tenant"] = "b";
        var stored = await sut.GetTriggerHeadersAsync(ctx.RunId);

        // Assert
        Assert.Equal("a", stored!["X-Tenant"]);
    }

    [Fact]
    public async Task MarkTimedOut_without_a_reason_keeps_an_existing_cancel_reason_like_the_sql_stores()
    {
        // Arrange — SQL Server and PostgreSQL set CancelReason = COALESCE(@Reason, CancelReason, 'Run timed out.').
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RequestCancelAsync(runId, "operator stop");

        // Act
        await store.MarkTimedOutAsync(runId, null);
        var control = await store.GetRunControlAsync(runId);

        // Assert
        Assert.Equal("operator stop", control!.CancelReason);
    }
    [Fact]
    public async Task MarkTimedOut_after_a_user_cancel_does_not_relabel_the_run_timed_out()
    {
        // Arrange — the sweep read the control record before the user's cancel landed; the cancel wins.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RequestCancelAsync(runId, "operator stop");

        // Act
        var applied = await store.MarkTimedOutAsync(runId, "Run exceeded its timeout deadline.");
        var control = await store.GetRunControlAsync(runId);

        // Assert
        Assert.False(applied);
        Assert.Null(control!.TimedOutAtUtc);
        Assert.True(control.CancelRequested);
        Assert.Equal("operator stop", control.CancelReason);
    }

    [Fact]
    public async Task ConfigureRun_on_an_existing_record_keeps_the_cancel_latch_like_the_sql_upserts()
    {
        // Arrange — SQL Server MERGE / PostgreSQL ON CONFLICT update only flow, trigger, key and deadline.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        await store.ConfigureRunAsync(runId, Guid.NewGuid(), "manual", null, null);
        await store.RequestCancelAsync(runId, "operator stop");

        // Act
        await store.ConfigureRunAsync(runId, flowId, "webhook", "key-1", deadline);
        var control = await store.GetRunControlAsync(runId);

        // Assert
        Assert.True(control!.CancelRequested);
        Assert.Equal("operator stop", control.CancelReason);
        Assert.NotNull(control.CancelRequestedAtUtc);
        Assert.Equal(flowId, control.FlowId);
        Assert.Equal("webhook", control.TriggerKey);
        Assert.Equal("key-1", control.IdempotencyKey);
        Assert.Equal(deadline, control.TimeoutAtUtc);
    }

    [Fact]
    public async Task RecordStepStart_keeps_the_evaluation_trace_like_the_sql_stores()
    {
        // Arrange — SQL Server / PostgreSQL RecordStepStartAsync upserts leave EvaluationTraceJson untouched.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "TraceFlow", runId, "manual", null, null);
        await store.RecordSkippedStepAsync(runId, "gate", "Work", "When clause false", "{\"expression\":\"x\"}");

        // Act
        await store.RecordStepStartAsync(runId, "gate", "Work", null, null);
        var detail = await store.GetRunDetailAsync(runId);

        // Assert
        var step = Assert.Single(detail!.Steps!, s => s.StepKey == "gate");
        Assert.Equal("{\"expression\":\"x\"}", step.EvaluationTraceJson);
    }

    [Fact]
    public async Task RecordSkippedStep_without_a_trace_keeps_the_previous_trace_like_the_sql_stores()
    {
        // Arrange — SQL Server ISNULL / PostgreSQL COALESCE(@trace, existing).
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await store.StartRunAsync(Guid.NewGuid(), "TraceFlow", runId, "manual", null, null);
        await store.RecordSkippedStepAsync(runId, "gate", "Work", "When clause false", "{\"expression\":\"x\"}");

        // Act
        await store.RecordSkippedStepAsync(runId, "gate", "Work", "Run is Cancelled.", null);
        var detail = await store.GetRunDetailAsync(runId);

        // Assert
        var step = Assert.Single(detail!.Steps!, s => s.StepKey == "gate");
        Assert.Equal("{\"expression\":\"x\"}", step.EvaluationTraceJson);
    }

    [Fact]
    public async Task Idempotency_key_longer_than_256_chars_deduplicates_and_is_kept_as_its_digest()
    {
        // Arrange — same normalisation as SQL Server / PostgreSQL (IdempotencyKeyStorage).
        var store = new InMemoryFlowRunStore();
        var flowId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var key = new string('k', 300);

        // Act
        var first = await store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key, runId);
        var duplicate = await store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", "  " + key.ToUpperInvariant() + " ", Guid.NewGuid());
        var other = await store.TryRegisterIdempotencyKeyAsync(flowId, "webhook", key + "x", Guid.NewGuid());
        var found = await store.FindRunIdByIdempotencyKeyAsync(flowId, "webhook", key);
        await store.ConfigureRunAsync(runId, flowId, "webhook", key, null);
        var control = await store.GetRunControlAsync(runId);

        // Assert
        Assert.True(first);
        Assert.False(duplicate);
        Assert.True(other);
        Assert.Equal(runId, found);
        Assert.Equal(IdempotencyKeyStorage.Normalize(key), control!.IdempotencyKey);
    }
}
