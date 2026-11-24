using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Dashboard.Notifications;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// Audit (4th sweep) — unit-level proofs for dashboard defects: the Microsoft Teams key encoding,
/// per-trigger rate-limit isolation and concurrent writes on the SSE response. Each test failed on the
/// audited commit.
/// </summary>
public sealed class SurfaceAuditUnitTests
{
    /// <summary>
    /// Microsoft's outgoing-webhook documentation (and its reference sample) derives the HMAC key by
    /// base64-DECODING the security token Teams shows in the portal (<c>Convert.FromBase64String(token)</c>).
    /// </summary>
    [Fact]
    public void MicrosoftTeams_verifies_signature_keyed_with_base64_decoded_security_token()
    {
        // Arrange
        const string securityToken = "dGVhbXMtc2VjdXJpdHktdG9rZW4tYnl0ZXMtMDEyMzQ1Ng=="; // as shown by Teams
        var body = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"text\":\"hello\"}");
        var teamsSignature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(securityToken), body));
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "HMAC " + teamsSignature },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.MicrosoftTeams),
            HmacKey = securityToken,
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.True(result.IsValid, result.Reason.ToString());
    }

    /// <summary>
    /// A Teams key that is not base64 cannot be the security token Teams issued: the verifier reports a
    /// configuration failure instead of throwing <see cref="FormatException"/> out of the request.
    /// </summary>
    [Fact]
    public void MicrosoftTeams_key_that_is_not_base64_fails_as_key_not_configured()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes("{}");
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "HMAC AAAA" },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.MicrosoftTeams),
            HmacKey = "not base64 !",
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(WebhookSignatureFailureReason.KeyNotConfigured, result.Reason);
    }

    /// <summary>
    /// Every other dialect keeps keying with the UTF-8 bytes of the configured key (GitHub here).
    /// </summary>
    [Fact]
    public void GitHub_still_keys_with_the_utf8_bytes_of_the_configured_key()
    {
        // Arrange
        const string key = "dGVhbXMtc2VjdXJpdHk="; // base64-looking, but GitHub keys with the literal string
        var body = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body)).ToLowerInvariant();
        var context = new WebhookSignatureContext
        {
            Body = body,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Hub-Signature-256"] = signature },
            Spec = PartnerSchemeRegistry.TryGet(WebhookSignatureScheme.GitHub),
            HmacKey = key,
        };

        // Act
        var result = new HmacSignatureVerifier().Verify(context);

        // Assert
        Assert.True(result.IsValid, result.Reason.ToString());
    }

    /// <summary>
    /// Two webhook triggers on the same flow with different <c>webhookRateLimitPermitsPerSecond</c> must
    /// not share one bucket sized by whichever trigger was hit first.
    /// </summary>
    [Fact]
    public async Task Rate_limit_budget_is_isolated_per_trigger_of_the_same_flow()
    {
        // Arrange
        var pipeline = new WebhookSecurityPipeline(
            new HmacSignatureVerifier(),
            new WebhookSecurityOptions { EnforcementMode = WebhookEnforcementMode.Enforce },
            rateLimiter: new TokenBucketWebhookRateLimiter());
        var flowId = Guid.NewGuid();
        var slow = new Dictionary<string, object?> { ["webhookRateLimitPermitsPerSecond"] = 1d, ["webhookRateLimitBurstSize"] = 1 };
        var fast = new Dictionary<string, object?> { ["webhookRateLimitPermitsPerSecond"] = 100d, ["webhookRateLimitBurstSize"] = 100 };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var http = new DefaultHttpContext();

        // Act
        var slowFirst = await pipeline.EvaluateAsync(http, flowId, "partner-a", slow, ReadOnlyMemory<byte>.Empty, headers);
        var fastFirst = await pipeline.EvaluateAsync(http, flowId, "partner-b", fast, ReadOnlyMemory<byte>.Empty, headers);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, slowFirst.Decision);
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, fastFirst.Decision);
    }

    /// <summary>
    /// WRONG RESULT (corrupt stream). <c>SseEndpointHandler</c> runs the heartbeat loop and the event
    /// loop concurrently against the same <see cref="HttpResponse"/>. Without synchronisation, while an
    /// event frame is being flushed (slow client / back-pressure), the heartbeat timer issued its own
    /// write: two concurrent writers on one response body, which ASP.NET Core does not support (frames
    /// interleave or the pipe throws).
    /// </summary>
    [Fact]
    public async Task Sse_heartbeat_never_writes_concurrently_with_an_event_frame()
    {
        // Arrange
        var body = new OverlapDetectingStream();
        var http = new DefaultHttpContext();
        http.Response.Body = body;
        using var aborted = new CancellationTokenSource();
        http.RequestAborted = aborted.Token;
        var broadcaster = new SseFlowEventBroadcaster();

        var handler = SseEndpointHandler.HandleAsync(http, broadcaster, runIdFilter: null, heartbeatInterval: TimeSpan.FromMilliseconds(5));
        await body.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(30)); // connection registered + timer running

        // Act
        await broadcaster.PublishAsync(new RunStartedEvent { RunId = Guid.NewGuid(), FlowName = "F" });
        var finished = await Task.WhenAny(body.Overlap.Task, body.EventWritten.Task).WaitAsync(TimeSpan.FromSeconds(30));
        var overlapped = finished == body.Overlap.Task;
        aborted.Cancel();
        await handler.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — writes are serialised: no heartbeat write enters while the event write is in flight.
        Assert.False(overlapped, "heartbeat and event frames were written concurrently on one response body");
    }

    /// <summary>
    /// Response body that holds the first event-frame write open (a slow client) and records whether
    /// any other write enters while it is in flight.
    /// </summary>
    private sealed class OverlapDetectingStream : Stream
    {
        private int _inFlight;
        private int _eventGated;

        /// <summary>Set when a second write starts while another is still in progress.</summary>
        public TaskCompletionSource<bool> Overlap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set when the gated event write completes without any overlap.</summary>
        public TaskCompletionSource<bool> EventWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set on the first heartbeat frame.</summary>
        public TaskCompletionSource<bool> FirstHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (Interlocked.Increment(ref _inFlight) > 1)
                Overlap.TrySetResult(true);
            try
            {
                if (text.Contains(": heartbeat", StringComparison.Ordinal))
                    FirstHeartbeat.TrySetResult(true);

                if (text.Contains("event: ", StringComparison.Ordinal) && Interlocked.Exchange(ref _eventGated, 1) == 0)
                {
                    // Slow client: keep this write in flight for several heartbeat periods (or until an
                    // overlapping write proves the race) — a logical event, with a generous cap.
                    await Task.WhenAny(Overlap.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
                    EventWritten.TrySetResult(true);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
