using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FlowOrchestrator.Dashboard.Notifications;

/// <summary>
/// Handles a single Server-Sent Events response: writes headers, registers a connection
/// against <see cref="SseFlowEventBroadcaster"/>, and pumps events + heartbeats until the
/// client disconnects or the host shuts down.
/// </summary>
/// <remarks>
/// <para>
/// SSE responses MUST NOT be wrapped in Brotli/Gzip — chunked compression buffers indefinitely
/// and breaks streaming. The handler writes directly to <see cref="HttpResponse.Body"/> with
/// <see cref="IHttpResponseBodyFeature.DisableBuffering"/> and an explicit flush after every write.
/// </para>
/// <para>
/// A heartbeat comment (<c>: heartbeat\n\n</c>) is emitted every <see cref="HeartbeatInterval"/>
/// to prevent intermediaries (nginx 60s, Azure Front Door 240s) from collapsing the connection
/// and to drive the client-side watchdog that activates polling fallback when the stream stalls.
/// </para>
/// </remarks>
public static class SseEndpointHandler
{
    /// <summary>Default heartbeat cadence — chosen to beat nginx's 60s default keep-alive.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _serializerOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string RetryDirective = "retry: 3000\n\n";
    private const string HeartbeatFrame = ": heartbeat\n\n";

    /// <summary>
    /// Runs the SSE response lifecycle on <paramref name="http"/>. Returns when the client
    /// disconnects, the broadcaster fails, or the application stops.
    /// </summary>
    /// <param name="http">The HTTP context for the SSE request.</param>
    /// <param name="broadcaster">The shared broadcaster to subscribe against.</param>
    /// <param name="runIdFilter">Optional run-id filter parsed from the query string.</param>
    /// <param name="heartbeatInterval">Override for tests; production should use the default.</param>
    public static async Task HandleAsync(
        HttpContext http,
        SseFlowEventBroadcaster broadcaster,
        Guid? runIdFilter,
        TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(broadcaster);

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";

        // Critical: never set Content-Encoding here. SSE relies on incremental writes; any
        // compression layer that buffers chunked output will hold events indefinitely.
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var ct = http.RequestAborted;

        // Send headers immediately so the EventSource enters readyState OPEN before the
        // first event arrives (some browsers wait for headers + one byte before firing onopen).
        await response.WriteAsync(RetryDirective, ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);

        var connection = new SseConnection(ct, runIdFilter);
        await using var registration = broadcaster.Register(connection);

        var heartbeat = new PeriodicTimer(heartbeatInterval ?? HeartbeatInterval);
        try
        {
            // Two cooperating loops: one drains events as they arrive, the other emits keep-alive
            // comments. Whichever completes (cancellation, channel closed, write failure) ends the
            // request.
            var heartbeatTask = HeartbeatLoopAsync(response, heartbeat, ct);
            var readerTask = ReaderLoopAsync(response, connection, ct);

            await Task.WhenAny(heartbeatTask, readerTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnect or host shutdown — expected, swallow.
        }
        catch (IOException)
        {
            // Broken pipe — also expected on disconnect.
        }
        finally
        {
            heartbeat.Dispose();
        }
    }

    private static async Task HeartbeatLoopAsync(HttpResponse response, PeriodicTimer timer, CancellationToken ct)
    {
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await response.WriteAsync(HeartbeatFrame, ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ReaderLoopAsync(HttpResponse response, SseConnection connection, CancellationToken ct)
    {
        await foreach (var evt in connection.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await WriteEventAsync(response, evt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Internal: render <paramref name="evt"/> as a single SSE frame. Public for unit-test ergonomics.
    /// </summary>
    public static async Task WriteEventAsync(HttpResponse response, FlowLifecycleEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<object>(evt, _serializerOptions);
        var frame = new StringBuilder(json.Length + 64)
            .Append("event: ").Append(evt.Type).Append('\n')
            .Append("id: ").Append(evt.RunId.ToString("N")).Append(':').Append(evt.At.ToUnixTimeMilliseconds()).Append('\n')
            .Append("data: ").Append(json).Append("\n\n")
            .ToString();

        await response.WriteAsync(frame, ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
