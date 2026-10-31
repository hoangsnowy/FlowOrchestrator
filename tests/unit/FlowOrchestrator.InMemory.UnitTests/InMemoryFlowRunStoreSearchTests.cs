using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryFlowRunStore.SearchRunsAsync"/> must match the SQL providers' semantics so the
/// dashboard behaves the same whatever the backend: run id by equality, text over the run's own
/// columns, step payloads only on request, <c>HasMore</c> instead of a count for searches.
/// </summary>
public sealed class InMemoryFlowRunStoreSearchTests
{
    private readonly InMemoryFlowRunStore _sut = new();

    private async Task<Guid> SeedRunAsync(string flowName, string status, DateTimeOffset startedAt, string? output = null, string? error = null, Guid? flowId = null)
    {
        var runId = Guid.NewGuid();
        await _sut.StartRunAsync(flowId ?? Guid.NewGuid(), flowName, runId, "manual", null, null);
        await _sut.RecordStepStartAsync(runId, "fetch", "Fetch", null, null);
        await _sut.RecordStepCompleteAsync(runId, "fetch", status, output, error);
        _sut.TryMutateRunForTests(runId, r => { r.Status = status; r.StartedAt = startedAt; });
        return runId;
    }

    [Fact]
    public async Task RunId_matches_exactly_one_run()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var target = await SeedRunAsync("A", "Succeeded", now);
        await SeedRunAsync("B", "Succeeded", now);

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { RunId = target });

        // Assert
        var run = Assert.Single(page.Runs);
        Assert.Equal(target, run.Id);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task Guid_text_matches_the_id_by_equality_not_substring()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var target = await SeedRunAsync("A", "Succeeded", now);
        await SeedRunAsync("B", "Succeeded", now);

        // Act
        var exact = await _sut.SearchRunsAsync(new RunSearchQuery { Text = target.ToString("D").ToUpperInvariant() });
        var fragment = await _sut.SearchRunsAsync(new RunSearchQuery { Text = target.ToString("D")[..8] });

        // Assert
        Assert.Equal(target, Assert.Single(exact.Runs).Id);
        Assert.Equal(target, Assert.Single(fragment.Runs).Id);
    }

    [Fact]
    public async Task Text_does_not_search_step_payloads_unless_asked()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var runId = await SeedRunAsync("PayFlow", "Failed", now, output: "{\"tx\":\"tx-7788\"}", error: "gateway timeout 504");

        // Act
        var byOutput = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "tx-7788" });
        var byError = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "gateway timeout" });
        var byStepKey = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "fetch" });
        var deepOutput = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "tx-7788", IncludeStepPayloads = true });
        var deepError = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "GATEWAY TIMEOUT", IncludeStepPayloads = true });

        // Assert
        Assert.Empty(byOutput.Runs);
        Assert.Empty(byError.Runs);
        Assert.Empty(byStepKey.Runs);
        Assert.Equal(runId, Assert.Single(deepOutput.Runs).Id);
        Assert.Equal(runId, Assert.Single(deepError.Runs).Id);
    }

    [Fact]
    public async Task Text_matches_the_run_columns_case_insensitively()
    {
        // Arrange
        var runId = await SeedRunAsync("InsuranceClaimSync", "Succeeded", DateTimeOffset.UtcNow);

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "claimsync" });

        // Assert
        Assert.Equal(runId, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task Window_bounds_the_search()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var recent = await SeedRunAsync("OrderFlow", "Failed", now.AddDays(-1));
        await SeedRunAsync("OrderFlow", "Failed", now.AddDays(-30));

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "order", StartedFrom = now.AddDays(-7) });

        // Assert
        Assert.Equal(recent, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task HasMore_is_true_only_when_a_row_follows_the_page()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await SeedRunAsync("OrderFlow", "Failed", now.AddMinutes(-i));

        // Act
        var first = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "order", Take = 2 });
        var last = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "order", Skip = 4, Take = 2 });
        var exact = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "order", Skip = 3, Take = 2 });

        // Assert
        Assert.Equal(2, first.Runs.Count);
        Assert.True(first.HasMore);
        Assert.Single(last.Runs);
        Assert.False(last.HasMore);
        Assert.Equal(2, exact.Runs.Count);
        Assert.False(exact.HasMore);
    }

    [Fact]
    public async Task Pages_are_newest_first()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var older = await SeedRunAsync("OrderFlow", "Failed", now.AddHours(-2));
        var newer = await SeedRunAsync("OrderFlow", "Failed", now.AddHours(-1));

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "order" });

        // Assert
        Assert.Equal(new[] { newer, older }, page.Runs.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Total_is_counted_only_without_a_text_or_run_id_term()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var flowId = Guid.NewGuid();
        var first = await SeedRunAsync("OrderFlow", "Succeeded", now, flowId: flowId);
        await SeedRunAsync("OrderFlow", "Succeeded", now, flowId: flowId);
        await SeedRunAsync("OrderFlow", "Failed", now, flowId: flowId);

        // Act
        var byStatus = await _sut.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Status = "succeeded", Take = 1, IncludeTotal = true });
        var byText = await _sut.SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Text = "order", IncludeTotal = true });
        var byId = await _sut.SearchRunsAsync(new RunSearchQuery { RunId = first, IncludeTotal = true });
        var notAsked = await _sut.SearchRunsAsync(new RunSearchQuery { FlowId = flowId });

        // Assert
        Assert.Equal(2, byStatus.TotalCount);
        Assert.True(byStatus.HasMore);
        Assert.Null(byText.TotalCount);
        Assert.Null(byId.TotalCount);
        Assert.Null(notAsked.TotalCount);
    }

    [Fact]
    public async Task Filters_are_and_combined_with_the_run_id()
    {
        // Arrange
        var runId = await SeedRunAsync("OrderFlow", "Failed", DateTimeOffset.UtcNow);

        // Act
        var wrongStatus = await _sut.SearchRunsAsync(new RunSearchQuery { RunId = runId, Status = "Succeeded" });
        var rightStatus = await _sut.SearchRunsAsync(new RunSearchQuery { RunId = runId, Status = "Failed" });

        // Assert
        Assert.Empty(wrongStatus.Runs);
        Assert.Single(rightStatus.Runs);
    }
}
