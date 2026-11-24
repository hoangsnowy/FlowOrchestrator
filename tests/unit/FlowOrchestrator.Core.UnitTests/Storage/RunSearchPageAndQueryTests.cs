using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// The shared page / query helpers every provider uses: <see cref="RunSearchPage.FromRows"/> (Take + 1,
/// next cursor, count cap) and <see cref="RunSearchQuery.IsRunIdPrefix"/>.
/// </summary>
public sealed class RunSearchPageAndQueryTests
{
    private static List<FlowRunRecord> Rows(int count)
    {
        var start = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<FlowRunRecord>(count);
        for (var i = 0; i < count; i++)
            rows.Add(new FlowRunRecord { Id = Guid.NewGuid(), StartedAt = start.AddMinutes(-i) });
        return rows;
    }

    [Fact]
    public void FromRows_trims_the_extra_row_and_points_the_cursor_at_the_last_kept_row()
    {
        // Arrange
        var rows = Rows(3);
        var lastKept = rows[1];

        // Act
        var page = RunSearchPage.FromRows(rows, take: 2, total: null, totalCountCap: null);

        // Assert
        Assert.Equal(2, page.Runs.Count);
        Assert.True(page.HasMore);
        Assert.Equal(new RunSearchCursor(lastKept.StartedAt, lastKept.Id), page.NextCursor);
    }

    [Fact]
    public void FromRows_has_no_cursor_on_the_last_page()
    {
        // Arrange
        var rows = Rows(2);

        // Act
        var page = RunSearchPage.FromRows(rows, take: 2, total: 2, totalCountCap: null);

        // Assert
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal(2, page.TotalCount);
        Assert.False(page.TotalIsLowerBound);
    }

    [Fact]
    public void FromRows_reports_a_count_past_the_cap_as_the_cap_and_a_lower_bound()
    {
        // Arrange — a capped COUNT over TOP (cap + 1) returns cap + 1 when more rows match.
        var rows = Rows(1);

        // Act
        var page = RunSearchPage.FromRows(rows, take: 50, total: 10_001, totalCountCap: 10_000);

        // Assert
        Assert.Equal(10_000, page.TotalCount);
        Assert.True(page.TotalIsLowerBound);
    }

    [Fact]
    public void FromRows_keeps_a_count_at_the_cap_exact()
    {
        // Arrange
        var rows = Rows(1);

        // Act
        var page = RunSearchPage.FromRows(rows, take: 50, total: 10_000, totalCountCap: 10_000);

        // Assert
        Assert.Equal(10_000, page.TotalCount);
        Assert.False(page.TotalIsLowerBound);
    }

    [Theory]
    [InlineData("bbdafe3e", true)]
    [InlineData("BBDAFE3E", true)]
    [InlineData("bbdafe3e-12", true)]
    [InlineData("1234", true)]
    [InlineData("abc", false)]
    [InlineData("----", false)]
    [InlineData("backfill", false)]
    [InlineData("bbdafe3e 12", false)]
    [InlineData("bbdafe3e-1234-5678-9abc-def012345678", false)]
    public void IsRunIdPrefix_accepts_hex_and_hyphens_between_four_and_thirty_five_characters(string text, bool expected)
    {
        // Arrange

        // Act
        var actual = RunSearchQuery.IsRunIdPrefix(text);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(10_000, 10_000)]
    public void EffectiveTotalCountCap_ignores_unusable_caps(int? cap, int? expected)
    {
        // Arrange
        var query = new RunSearchQuery { TotalCountCap = cap };

        // Act
        var effective = query.EffectiveTotalCountCap;

        // Assert
        Assert.Equal(expected, effective);
    }
}
