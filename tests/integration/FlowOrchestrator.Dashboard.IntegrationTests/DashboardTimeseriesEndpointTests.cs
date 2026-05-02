using System.Net;
using System.Text.Json;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration tests for the <c>/api/runs/timeseries</c> endpoint that powers the
/// 24h activity heatmap, 30-day calendar, and per-flow health badges. Verifies
/// the endpoint forwards bucket / window / flowId parameters correctly to
/// <see cref="IFlowRunStore.GetRunTimeseriesAsync"/> and shapes the JSON envelope
/// the dashboard JS expects.
/// </summary>
public sealed class DashboardTimeseriesEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public DashboardTimeseriesEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task GET_timeseries_default_returns_24_hour_buckets()
    {
        // Arrange
        var bucket = new RunTimeseriesBucket
        {
            Timestamp = DateTimeOffset.UtcNow,
            Total = 5,
            Succeeded = 4,
            Failed = 1,
            P95DurationMs = 120
        };
        _server.FlowRunStore
            .GetRunTimeseriesAsync(RunTimeseriesGranularity.Hour, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), null)
            .Returns([bucket]);

        // Act
        var response = await _client.GetAsync("/flows/api/runs/timeseries");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("hour", doc.RootElement.GetProperty("bucket").GetString());
        var buckets = doc.RootElement.GetProperty("buckets");
        Assert.Equal(1, buckets.GetArrayLength());
        Assert.Equal(5, buckets[0].GetProperty("total").GetInt32());
        Assert.Equal(120, buckets[0].GetProperty("p95DurationMs").GetDouble());
    }

    [Fact]
    public async Task GET_timeseries_with_bucket_day_uses_day_granularity()
    {
        // Arrange
        _server.FlowRunStore
            .GetRunTimeseriesAsync(RunTimeseriesGranularity.Day, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), null)
            .Returns([new RunTimeseriesBucket { Timestamp = DateTimeOffset.UtcNow.Date, Total = 3 }]);

        // Act
        var response = await _client.GetAsync("/flows/api/runs/timeseries?bucket=day&days=30");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("day", doc.RootElement.GetProperty("bucket").GetString());
        await _server.FlowRunStore.Received(1).GetRunTimeseriesAsync(
            RunTimeseriesGranularity.Day,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            null);
    }

    [Fact]
    public async Task GET_timeseries_with_flowId_filters_to_flow()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        _server.FlowRunStore
            .GetRunTimeseriesAsync(Arg.Any<RunTimeseriesGranularity>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), flowId)
            .Returns([]);

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/timeseries?bucket=hour&hours=24&flowId={flowId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).GetRunTimeseriesAsync(
            RunTimeseriesGranularity.Hour,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            flowId);
    }

    [Fact]
    public async Task GET_timeseries_clamps_hours_param_to_safety_max()
    {
        // Arrange — caller asks for an absurd 100 000-hour window. The endpoint clamps to 720h (30 days)
        // so the SQL query window stays bounded.
        _server.FlowRunStore
            .GetRunTimeseriesAsync(Arg.Any<RunTimeseriesGranularity>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), null)
            .Returns([]);

        // Act
        var response = await _client.GetAsync("/flows/api/runs/timeseries?bucket=hour&hours=100000");

        // Assert — call lands; we just need to confirm the endpoint did not pass through 100k hours.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowRunStore.Received(1).GetRunTimeseriesAsync(
            RunTimeseriesGranularity.Hour,
            Arg.Is<DateTimeOffset>(s => (DateTimeOffset.UtcNow - s) < TimeSpan.FromDays(31)),
            Arg.Any<DateTimeOffset>(),
            null);
    }

    [Fact]
    public async Task GET_timeseries_emits_vary_accept_encoding_header()
    {
        // Arrange
        _server.FlowRunStore
            .GetRunTimeseriesAsync(Arg.Any<RunTimeseriesGranularity>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), null)
            .Returns([]);

        // Act
        var response = await _client.GetAsync("/flows/api/runs/timeseries");

        // Assert — JSON endpoints must declare Vary so caches don't serve compressed bytes to a no-encoding client.
        Assert.Contains(response.Headers.Vary, h => string.Equals(h, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }
}
