using System.Net;
using System.Text.Json;
using FlowOrchestrator.Core.Notifications;

namespace FlowOrchestrator.Dashboard.Tests.Notifications;

/// <summary>
/// End-to-end tests for the dashboard's <c>GET /flows/api/events/stream</c> SSE endpoint.
/// Verifies the response shape (content-type, no compression) and that events published
/// through the broadcaster reach the streaming client.
/// </summary>
public sealed class SseEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public SseEndpointTests()
    {
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Stream_returns_event_stream_content_type_with_no_encoding()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/events/stream");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        // Compression must NOT be applied — chunked Brotli buffers indefinitely and breaks SSE.
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Stream_emits_retry_directive_immediately()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/events/stream");

        // Act — read the first chunk of the stream within the timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var buffer = new char[64];
        var read = await reader.ReadAsync(buffer.AsMemory(), cts.Token);
        var firstChunk = new string(buffer, 0, read);

        // Assert — clients should see "retry: 3000" before any event so reconnect cadence is predictable.
        Assert.StartsWith("retry: 3000", firstChunk);
    }

    [Fact]
    public async Task Published_event_reaches_connected_client()
    {
        // Arrange — open the stream first so the broadcaster has a registered connection.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/events/stream");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Drain the leading retry directive so the next read sees the published event.
        await ReadFrameAsync(reader, cts.Token);

        // Wait for the broadcaster to register the connection (the handler runs async after headers).
        await WaitForConnectionAsync(_server, expected: 1, cts.Token);

        var runId = Guid.NewGuid();
        var evt = new RunStartedEvent
        {
            RunId = runId,
            FlowId = Guid.NewGuid(),
            FlowName = "TestFlow",
            TriggerKey = "manual"
        };

        // Act
        await _server.Broadcaster.PublishAsync(evt, cts.Token);
        var frame = await ReadFrameAsync(reader, cts.Token);

        // Assert
        Assert.Contains("event: run.started", frame);
        Assert.Contains("data: ", frame);
        var dataLine = frame.Split('\n').First(l => l.StartsWith("data: "));
        var json = dataLine["data: ".Length..];
        var doc = JsonDocument.Parse(json);
        Assert.Equal(runId.ToString(), doc.RootElement.GetProperty("runId").GetString());
        Assert.Equal("TestFlow", doc.RootElement.GetProperty("flowName").GetString());
    }

    [Fact]
    public async Task RunIdFilter_excludes_unrelated_events()
    {
        // Arrange — connect with ?runId=<matching>
        var matching = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/flows/api/events/stream?runId={matching}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        await ReadFrameAsync(reader, cts.Token); // discard retry directive
        await WaitForConnectionAsync(_server, expected: 1, cts.Token);

        // Act — publish unrelated first, then matching. The matching one should arrive next.
        await _server.Broadcaster.PublishAsync(new RunCompletedEvent { RunId = unrelated, Status = "Succeeded" }, cts.Token);
        await _server.Broadcaster.PublishAsync(new RunCompletedEvent { RunId = matching, Status = "Failed" }, cts.Token);

        var frame = await ReadFrameAsync(reader, cts.Token);

        // Assert — first frame after subscription is the matching event, not the unrelated one.
        Assert.Contains(matching.ToString(), frame);
        Assert.DoesNotContain(unrelated.ToString(), frame);
    }

    [Fact]
    public async Task Disconnect_removes_connection_from_broadcaster()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/flows/api/events/stream");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        await WaitForConnectionAsync(_server, expected: 1, cts.Token);

        // Act — disposing the response cancels the request, which fires RequestAborted on the server.
        response.Dispose();

        // Assert — eventually the broadcaster observes the disconnection and deregisters.
        await WaitForConnectionAsync(_server, expected: 0, cts.Token);
    }

    /// <summary>Reads one SSE frame (terminated by a blank line) from the stream.</summary>
    private static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (sb.Length == 0) continue; // skip empty lines between frames
                return sb.ToString();
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Polls the broadcaster's registered-connection count until it reaches <paramref name="expected"/> or the token cancels.</summary>
    private static async Task WaitForConnectionAsync(DashboardTestServer server, int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (server.Broadcaster.ConnectionCount == expected) return;
            await Task.Delay(50, ct);
        }
        Assert.Equal(expected, server.Broadcaster.ConnectionCount);
    }
}
