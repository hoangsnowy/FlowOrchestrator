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
}
