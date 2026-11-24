using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// 2.0 run-list contract on the in-memory store: keyset cursor, capped total, and the run-id prefix that
/// matches at any age — the same behaviour the SQL Server store has, so the dashboard does not depend on
/// the backend.
/// </summary>
public sealed class InMemoryFlowRunStoreKeysetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryFlowRunStore _sut = new();

    private async Task<Guid> SeedRunAsync(DateTimeOffset startedAt, string flowName = "KeysetFlow", Guid? runId = null)
    {
        var id = runId ?? Guid.NewGuid();
        await _sut.StartRunAsync(Guid.NewGuid(), flowName, id, "manual", null, null);
        _sut.TryMutateRunForTests(id, r => r.StartedAt = startedAt);
        return id;
    }

    [Fact]
    public async Task Cursor_paging_is_stable_while_new_runs_start()
    {
        // Arrange
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++) seeded.Add(await SeedRunAsync(Now.AddMinutes(-i)));
        var first = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 2 });
        await SeedRunAsync(Now.AddMinutes(1));   // a new run at the head of the list
        await SeedRunAsync(Now.AddMinutes(2));

        // Act
        var second = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 2, After = first.NextCursor });
        var third = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 2, After = second.NextCursor });

        // Assert — no row repeats and none is skipped, although two runs were inserted at the head.
        Assert.Equal(new[] { seeded[0], seeded[1] }, first.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[2], seeded[3] }, second.Runs.Select(r => r.Id));
        Assert.Equal(new[] { seeded[4] }, third.Runs.Select(r => r.Id));
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task Cursor_breaks_ties_between_equal_start_times()
    {
        // Arrange
        for (var i = 0; i < 5; i++) await SeedRunAsync(Now);

        // Act
        var first = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 3 });
        var second = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 3, After = first.NextCursor });

        // Assert
        var all = first.Runs.Concat(second.Runs).Select(r => r.Id).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
    }

    [Fact]
    public async Task Capped_total_reports_the_cap_as_a_lower_bound()
    {
        // Arrange
        for (var i = 0; i < 6; i++) await SeedRunAsync(Now.AddMinutes(-i));

        // Act
        var capped = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 2, IncludeTotal = true, TotalCountCap = 4 });
        var exact = await _sut.SearchRunsAsync(new RunSearchQuery { Take = 2, IncludeTotal = true });

        // Assert
        Assert.Equal(4, capped.TotalCount);
        Assert.True(capped.TotalIsLowerBound);
        Assert.Equal(6, exact.TotalCount);
        Assert.False(exact.TotalIsLowerBound);
    }

    [Fact]
    public async Task Run_id_prefix_finds_an_old_run_outside_the_window()
    {
        // Arrange
        var old = await SeedRunAsync(Now.AddDays(-60));
        await SeedRunAsync(Now);
        var prefix = old.ToString("D")[..8].ToUpperInvariant();

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = prefix, StartedFrom = Now.AddDays(-7) });

        // Assert
        Assert.Equal(old, Assert.Single(page.Runs).Id);
    }

    [Fact]
    public async Task Non_hex_text_stays_inside_the_window()
    {
        // Arrange
        await SeedRunAsync(Now.AddDays(-60), flowName: "ArchivedFlow");

        // Act
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = "Archived", StartedFrom = Now.AddDays(-7) });

        // Assert
        Assert.Empty(page.Runs);
    }
}
