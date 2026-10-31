using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// <see cref="PostgreSqlFlowRunStore.SearchRunsAsync"/> against a real PostgreSQL: run id by equality, text over
/// the run's own columns only, step payloads on request, <c>HasMore</c> from <c>Take + 1</c>, and a total
/// only for plain filters. Each test scopes its rows by a fresh flow id because the container is shared.
/// </summary>
public sealed partial class PostgreSqlFlowRunStoreTests
{
    private async Task<Guid> SeedSearchRunAsync(Guid flowId, string flowName, string status, DateTimeOffset startedAt, string triggerKey = "manual", string? output = null, string? error = null)
    {
        var runId = Guid.NewGuid();
        await _store.StartRunAsync(flowId, flowName, runId, triggerKey, null, null);
        await _store.RecordStepStartAsync(runId, "fetch", "Fetch", null, null);
        await _store.RecordStepCompleteAsync(runId, "fetch", status, output, error);
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("UPDATE flow_runs SET status = @status, started_at = @startedAt WHERE id = @runId", new { status, startedAt, runId });
        return runId;
    }

    [Fact]
    public async Task SearchRunsAsync_run_id_returns_exactly_that_run()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var target = await SeedSearchRunAsync(flowId, "IdFlow", "Succeeded", now);
        await SeedSearchRunAsync(flowId, "IdFlow", "Succeeded", now);

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { RunId = target, IncludeTotal = true });

        // Assert
        Assert.Equal(target, Assert.Single(page.Runs).Id);
        Assert.False(page.HasMore);
        Assert.Null(page.TotalCount);
    }

    [Fact]
    public async Task SearchRunsAsync_guid_text_matches_the_id_and_a_fragment_still_works()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var target = await SeedSearchRunAsync(flowId, "GuidTextFlow", "Failed", DateTimeOffset.UtcNow);

        // Act
        var exact = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = target.ToString("D").ToUpperInvariant() });
        var fragment = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = target.ToString("D")[..13] });

        // Assert
        Assert.Equal(target, Assert.Single(exact.Runs).Id);
        Assert.Equal(target, Assert.Single(fragment.Runs).Id);
    }

    [Fact]
    public async Task SearchRunsAsync_text_does_not_read_step_payloads_unless_asked()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var token = $"tx{Guid.NewGuid():N}";
        var runId = await SeedSearchRunAsync(flowId, "PayloadFlow", "Failed", DateTimeOffset.UtcNow,
            output: $"{{\"transactionId\":\"{token}\"}}", error: $"gateway {token}-err");

        // Act
        var quick = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = token });
        var deep = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = token, IncludeStepPayloads = true });
        var deepError = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = token + "-err", IncludeStepPayloads = true });

        // Assert
        Assert.Empty(quick.Runs);
        Assert.Equal(runId, Assert.Single(deep.Runs).Id);
        Assert.Equal(runId, Assert.Single(deepError.Runs).Id);
    }

    [Fact]
    public async Task SearchRunsAsync_text_matches_trigger_key_fragment_inside_the_window()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var recent = await SeedSearchRunAsync(flowId, "WindowFlow", "Skipped", now.AddDays(-1), triggerKey: "webhook-claim-00042");
        await SeedSearchRunAsync(flowId, "WindowFlow", "Skipped", now.AddDays(-30), triggerKey: "webhook-claim-00042");

        // Act
        var windowed = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "claim-0004", StartedFrom = now.AddDays(-7) });
        var unbounded = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "claim-0004" });

        // Assert
        Assert.Equal(recent, Assert.Single(windowed.Runs).Id);
        Assert.Equal(2, unbounded.Runs.Count);
    }

    [Fact]
    public async Task SearchRunsAsync_escapes_like_wildcards()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var literal = await SeedSearchRunAsync(flowId, "Escape_100%[x]", "Succeeded", now);
        await SeedSearchRunAsync(flowId, "EscapeA100Zx", "Succeeded", now);

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "_100%[x]" });

        // Assert
        Assert.Equal(literal, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task SearchRunsAsync_has_more_comes_from_take_plus_one_newest_first()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await SeedSearchRunAsync(flowId, "PagedFlow", "Failed", now.AddMinutes(-i)));

        // Act
        var first = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "PagedFlow", Take = 2 });
        var tail = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "PagedFlow", Skip = 3, Take = 2 });

        // Assert
        Assert.Equal(ids.Take(2), first.Runs.Select(r => r.Id));
        Assert.True(first.HasMore);
        Assert.Equal(ids.Skip(3), tail.Runs.Select(r => r.Id));
        Assert.False(tail.HasMore);
        Assert.Null(first.TotalCount);
    }

    [Fact]
    public async Task SearchRunsAsync_counts_total_only_for_plain_filters()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedSearchRunAsync(flowId, "CountFlow", "Succeeded", now);
        await SeedSearchRunAsync(flowId, "CountFlow", "Succeeded", now);
        await SeedSearchRunAsync(flowId, "CountFlow", "Failed", now);

        // Act
        var byStatus = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Status = "Succeeded", Take = 1, IncludeTotal = true });
        var pastEnd = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Skip = 50, IncludeTotal = true });
        var byText = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "CountFlow", IncludeTotal = true });

        // Assert
        Assert.Equal(2, byStatus.TotalCount);
        Assert.True(byStatus.HasMore);
        Assert.Empty(pastEnd.Runs);
        Assert.Equal(3, pastEnd.TotalCount);
        Assert.Null(byText.TotalCount);
    }

    [Fact]
    public async Task GetRunsAsync_still_lists_newest_first()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var older = await SeedSearchRunAsync(flowId, "ListFlow", "Succeeded", now.AddMinutes(-1));
        var newer = await SeedSearchRunAsync(flowId, "ListFlow", "Succeeded", now);

        // Act
        var runs = await _store.GetRunsAsync(flowId, 0, 10);

        // Assert
        Assert.Equal(new[] { newer, older }, runs.Select(r => r.Id));
    }

    [Fact]
    public async Task SearchRunsAsync_non_positive_take_returns_an_empty_page_instead_of_throwing()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        await SeedSearchRunAsync(flowId, "TakeFlow", "Failed", DateTimeOffset.UtcNow);

        // Act
        var negative = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = -1 });
        var zero = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Take = 0 });

        // Assert
        Assert.Empty(negative.Runs);
        Assert.True(negative.HasMore);
        Assert.Empty(zero.Runs);
        Assert.True(zero.HasMore);
    }

    [Fact]
    public async Task SearchRunsAsync_accepts_a_window_with_a_non_utc_offset()
    {
        // Arrange — the dashboard normalises to UTC, but direct store callers may pass any offset.
        var flowId = Guid.NewGuid();
        var runId = await SeedSearchRunAsync(flowId, "OffsetFlow", "Failed", DateTimeOffset.UtcNow.AddHours(-1));
        var from = DateTimeOffset.UtcNow.AddHours(-2).ToOffset(TimeSpan.FromHours(7));

        // Act
        var page = await _store.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, StartedFrom = from });

        // Assert
        Assert.Equal(runId, Assert.Single(page.Runs).Id);
    }
}
