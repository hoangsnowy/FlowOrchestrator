using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace FlowOrchestrator.Dashboard.UnitTests.RunSearch;

/// <summary>
/// Pins how <c>GET /api/runs</c> query strings become a <see cref="FlowOrchestrator.Core.Storage.RunSearchQuery"/>:
/// the default search window, opt-in step payload search, and when a total may be counted.
/// </summary>
public sealed class RunListRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private static RunListRequest Parse(string queryString, FlowDashboardOptions? options = null)
    {
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        return RunListRequest.Parse(new QueryCollection(parsed), options ?? new FlowDashboardOptions(), Now);
    }

    [Fact]
    public void Text_search_without_a_window_defaults_to_seven_days()
    {
        // Arrange

        // Act
        var request = Parse("?search=order-42");

        // Assert
        Assert.Equal(Now.AddDays(-7), request.Query.StartedFrom);
        Assert.Null(request.Query.StartedTo);
        Assert.True(request.WindowDefaulted);
        Assert.Equal("order-42", request.Query.Text);
    }

    [Fact]
    public void Text_search_window_follows_the_configured_option()
    {
        // Arrange
        var options = new FlowDashboardOptions { RunSearchWindow = TimeSpan.FromDays(30) };

        // Act
        var request = Parse("?search=order-42", options);

        // Assert
        Assert.Equal(Now.AddDays(-30), request.Query.StartedFrom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_window_option_falls_back_to_the_default(int days)
    {
        // Arrange — the window is what bounds a payload search; it cannot be switched off.
        var options = new FlowDashboardOptions { RunSearchWindow = TimeSpan.FromDays(days) };

        // Act
        var request = Parse("?search=order-42&deep=true", options);

        // Assert
        Assert.Equal(Now - FlowDashboardOptions.DefaultRunSearchWindow, request.Query.StartedFrom);
    }

    [Fact]
    public void Text_search_with_only_an_upper_bound_anchors_the_window_on_it()
    {
        // Arrange
        var to = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var request = Parse("?search=order-42&to=2026-09-01T00:00:00Z");

        // Assert
        Assert.Equal(to.AddDays(-7), request.Query.StartedFrom);
        Assert.Equal(to, request.Query.StartedTo);
        Assert.True(request.WindowDefaulted);
    }

    [Fact]
    public void Text_search_with_an_explicit_lower_bound_keeps_it()
    {
        // Arrange

        // Act
        var request = Parse("?search=order-42&from=2026-01-01T00:00:00Z");

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), request.Query.StartedFrom);
        Assert.False(request.WindowDefaulted);
    }

    [Theory]
    [InlineData("?search=3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("?search=Completed")]
    [InlineData("?search=Failed")]
    [InlineData("?status=Failed")]
    [InlineData("?includeTotal=true")]
    public void Run_id_status_and_plain_filters_are_not_windowed(string queryString)
    {
        // Arrange — these are index seeks; windowing them would hide older runs for no gain.

        // Act
        var request = Parse(queryString);

        // Assert
        Assert.Null(request.Query.StartedFrom);
        Assert.False(request.WindowDefaulted);
    }

    [Theory]
    [InlineData("?search=order-42")]
    [InlineData("?search=order-42&deep=false")]
    [InlineData("?search=order-42&deep=0")]
    [InlineData("?search=order-42&deep=nope")]
    public void Step_payload_search_is_off_by_default(string queryString)
    {
        // Arrange

        // Act
        var request = Parse(queryString);

        // Assert
        Assert.False(request.Query.IncludeStepPayloads);
    }

    [Theory]
    [InlineData("?search=order-42&deep=true")]
    [InlineData("?search=order-42&deep=TRUE")]
    [InlineData("?search=order-42&deep=1")]
    public void Step_payload_search_is_opt_in_and_windowed(string queryString)
    {
        // Arrange

        // Act
        var request = Parse(queryString);

        // Assert
        Assert.True(request.Query.IncludeStepPayloads);
        Assert.Equal(Now.AddDays(-7), request.Query.StartedFrom);
    }

    [Fact]
    public void Step_payload_flag_is_dropped_when_the_term_is_a_status()
    {
        // Arrange

        // Act
        var request = Parse("?search=Completed&deep=true");

        // Assert
        Assert.False(request.Query.IncludeStepPayloads);
        Assert.Equal("Succeeded", request.Query.Status);
        Assert.Null(request.Query.Text);
    }

    [Fact]
    public void Status_term_becomes_the_status_filter_and_keeps_an_exact_total()
    {
        // Arrange

        // Act
        var request = Parse("?search=Completed&includeTotal=true");

        // Assert
        Assert.Equal("Succeeded", request.Query.Status);
        Assert.True(request.Query.CountsTotal);
    }

    [Fact]
    public void Explicit_status_filter_wins_over_a_status_term()
    {
        // Arrange

        // Act
        var request = Parse("?status=Failed&search=Succeeded&includeTotal=true");

        // Assert
        Assert.Equal("Failed", request.Query.Status);
        Assert.Equal("Succeeded", request.Query.Text);
        Assert.False(request.Query.CountsTotal);
    }

    [Theory]
    [InlineData("?search=order-42&includeTotal=true")]
    [InlineData("?search=3f2504e0-4f89-11d3-9a0c-0305e82c3301&includeTotal=true")]
    public void Text_and_run_id_searches_never_count(string queryString)
    {
        // Arrange

        // Act
        var request = Parse(queryString);

        // Assert
        Assert.True(request.IncludeTotal);
        Assert.False(request.Query.CountsTotal);
    }

    [Fact]
    public void No_filters_is_the_plain_listing()
    {
        // Arrange

        // Act
        var request = Parse("?take=10");

        // Assert
        Assert.True(request.IsPlainListing);
        Assert.Equal(10, request.Query.Take);
    }

    [Theory]
    [InlineData("?take=0", 1)]
    [InlineData("?take=100000", 500)]
    [InlineData("?take=abc", 50)]
    public void Take_is_clamped(string queryString, int expected)
    {
        // Arrange

        // Act
        var request = Parse(queryString);

        // Assert
        Assert.Equal(expected, request.Query.Take);
    }

    [Theory]
    [InlineData("2026-10-01T07:00:00+07:00")]
    [InlineData("2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T00:00:00")]
    public void Window_bounds_are_normalised_to_utc(string from)
    {
        // Arrange — Npgsql refuses a non-zero offset for timestamptz; no offset means UTC.

        // Act
        var request = Parse("?search=x&from=" + Uri.EscapeDataString(from) + "&to=" + Uri.EscapeDataString(from));

        // Assert
        Assert.Equal(TimeSpan.Zero, request.Query.StartedFrom!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), request.Query.StartedFrom);
        Assert.Equal(TimeSpan.Zero, request.Query.StartedTo!.Value.Offset);
    }
}
