using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// <c>GET /api/runs/{runId}/events</c> — the dashboard's "Events" drawer. Since 2.0 the timeline is
/// derived from the run record and its step attempts instead of a persisted event log; these tests
/// pin the JSON shape <c>dashboard.js</c> reads (<c>timestamp</c>, <c>type</c>, <c>stepKey</c>,
/// <c>message</c>) and the endpoint's paging and compression contract.
/// </summary>
public sealed class RunEventsEndpointTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public RunEventsEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    private static FlowRunRecord RetriedRun(Guid runId) => new()
    {
        Id = runId,
        Status = "Succeeded",
        TriggerKey = "manual",
        StartedAt = T0,
        CompletedAt = T0.AddSeconds(10),
        Steps =
        [
            new FlowStepRecord
            {
                RunId = runId,
                StepKey = "charge",
                StepType = "Charge",
                Status = "Succeeded",
                StartedAt = T0.AddSeconds(1),
                CompletedAt = T0.AddSeconds(6),
                Attempts =
                [
                    new FlowStepAttemptRecord { RunId = runId, StepKey = "charge", Attempt = 1, StepType = "Charge", Status = "Failed", ErrorMessage = "card declined", StartedAt = T0.AddSeconds(1), CompletedAt = T0.AddSeconds(2) },
                    new FlowStepAttemptRecord { RunId = runId, StepKey = "charge", Attempt = 2, StepType = "Charge", Status = "Succeeded", StartedAt = T0.AddSeconds(5), CompletedAt = T0.AddSeconds(6) }
                ]
            },
            new FlowStepRecord
            {
                RunId = runId,
                StepKey = "notify",
                StepType = "Notify",
                Status = "Skipped",
                ErrorMessage = StepSkipReasons.PrerequisitesUnmet,
                StartedAt = T0.AddSeconds(3),
                CompletedAt = T0.AddSeconds(3),
                Attempts =
                [
                    new FlowStepAttemptRecord { RunId = runId, StepKey = "notify", Attempt = 1, StepType = "Notify", Status = "Skipped", ErrorMessage = StepSkipReasons.PrerequisitesUnmet, StartedAt = T0.AddSeconds(3), CompletedAt = T0.AddSeconds(3) }
                ]
            }
        ]
    };

    [Fact]
    public async Task GET_events_returns_the_timeline_derived_from_the_run_and_its_attempts()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(RetriedRun(runId));

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{runId}/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(
            ["run.started", "step.started", "step.failed", "step.skipped", "step.started", "step.completed", "run.completed"],
            entries.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], entries.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.All(entries, e => Assert.Equal(runId, e.GetProperty("runId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("stepKey").ValueKind);
        Assert.Equal("charge", entries[2].GetProperty("stepKey").GetString());
        Assert.Equal("card declined", entries[2].GetProperty("message").GetString());
        Assert.Equal(StepSkipReasons.PrerequisitesUnmet, entries[3].GetProperty("message").GetString());
        Assert.Equal(T0.AddSeconds(2), entries[2].GetProperty("timestamp").GetDateTimeOffset());
    }

    [Fact]
    public async Task GET_events_for_an_unknown_run_returns_an_empty_array()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{Guid.NewGuid()}/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GET_events_pages_the_derived_timeline_with_skip_and_take()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(RetriedRun(runId));

        // Act
        var response = await _client.GetAsync($"/flows/api/runs/{runId}/events?skip=2&take=2");

        // Assert
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal([3L, 4], entries.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal(["step.failed", "step.skipped"], entries.Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task GET_events_brotli_and_gzip_decode_to_the_uncompressed_json()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId).Returns(RetriedRun(runId));
        var url = $"/flows/api/runs/{runId}/events";
        using var rawResponse = await _client.GetAsync(url);
        var rawJson = await rawResponse.Content.ReadAsStringAsync();

        using var brRequest = new HttpRequestMessage(HttpMethod.Get, url);
        brRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        using var gzRequest = new HttpRequestMessage(HttpMethod.Get, url);
        gzRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Act
        using var brResponse = await _client.SendAsync(brRequest);
        using var gzResponse = await _client.SendAsync(gzRequest);

        // Assert
        Assert.Contains("br", brResponse.Content.Headers.ContentEncoding);
        Assert.Contains("gzip", gzResponse.Content.Headers.ContentEncoding);
        Assert.Contains(rawResponse.Headers.Vary, v => string.Equals(v, "Accept-Encoding", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(rawJson, await DecodeAsync(brResponse, s => new BrotliStream(s, CompressionMode.Decompress)));
        Assert.Equal(rawJson, await DecodeAsync(gzResponse, s => new GZipStream(s, CompressionMode.Decompress)));
    }

    private static async Task<string> DecodeAsync(HttpResponseMessage response, Func<Stream, Stream> decompress)
    {
        using var compressed = await response.Content.ReadAsStreamAsync();
        using var decompressor = decompress(compressed);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
