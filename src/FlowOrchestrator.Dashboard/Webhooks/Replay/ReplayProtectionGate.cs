using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Webhooks.Replay;

/// <summary>
/// Pure helper that decides whether a webhook receive is a replay. Owns the
/// timestamp-skew check + nonce registration; persistence is delegated to
/// <see cref="IWebhookReplayStore"/>.
/// </summary>
public sealed class ReplayProtectionGate
{
    private readonly IWebhookReplayStore _store;
    private readonly TimeProvider _clock;

    /// <summary>Wraps a replay store + clock.</summary>
    /// <param name="store">Persistent nonce store.</param>
    /// <param name="clock">Time provider used for skew comparison + expiry.</param>
    public ReplayProtectionGate(IWebhookReplayStore store, TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Outcome of <see cref="EvaluateAsync"/>.</summary>
    public enum Decision
    {
        /// <summary>Skipped — replay protection not configured.</summary>
        Disabled,

        /// <summary>Accepted as a fresh delivery.</summary>
        Fresh,

        /// <summary>Rejected because the timestamp is outside the configured skew window.</summary>
        SkewRejected,

        /// <summary>Rejected because the same nonce has already been registered.</summary>
        ReplayRejected,

        /// <summary>Rejected because no usable timestamp could be located.</summary>
        TimestampMissing,
    }

    /// <summary>Verdict + the wall-clock skew measured for diagnostics.</summary>
    public readonly record struct ReplayResult(Decision Decision, TimeSpan Skew, string? Reason);

    /// <summary>
    /// Evaluates the request. Returns <see cref="Decision.Disabled"/> when
    /// <paramref name="toleranceSeconds"/> is non-positive — operators opt into
    /// replay protection by setting it on <see cref="WebhookSecurityOptions"/> or
    /// the trigger manifest.
    /// </summary>
    /// <param name="flowId">Resolved flow identifier (scopes the nonce table).</param>
    /// <param name="triggerKey">Resolved trigger key.</param>
    /// <param name="headers">Inbound headers; case-insensitive lookup expected.</param>
    /// <param name="body">Raw body bytes (used to derive a default nonce when no header is configured).</param>
    /// <param name="toleranceSeconds">Maximum allowed skew between client + server clocks.</param>
    /// <param name="timestampHeader">Optional explicit timestamp header (e.g. <c>X-Webhook-Timestamp</c>).</param>
    /// <param name="nonceHeader">Optional explicit nonce header (e.g. <c>X-Webhook-Delivery-Id</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<ReplayResult> EvaluateAsync(
        Guid flowId,
        string triggerKey,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        int toleranceSeconds,
        string? timestampHeader,
        string? nonceHeader,
        CancellationToken ct = default)
    {
        if (toleranceSeconds <= 0)
            return new ReplayResult(Decision.Disabled, TimeSpan.Zero, null);

        // Locate timestamp: explicit header first, fall back to common standards.
        var tsString = TryHeader(headers, timestampHeader)
                       ?? TryHeader(headers, "X-Webhook-Timestamp")
                       ?? TryHeader(headers, "X-Slack-Request-Timestamp")
                       ?? TryHeader(headers, "X-Zm-Request-Timestamp");
        if (string.IsNullOrWhiteSpace(tsString))
            return new ReplayResult(Decision.TimestampMissing, TimeSpan.Zero, "missing_timestamp");

        if (!long.TryParse(tsString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return new ReplayResult(Decision.TimestampMissing, TimeSpan.Zero, "invalid_timestamp");

        var ts = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var now = _clock.GetUtcNow();
        var skew = (now - ts).Duration();
        if (skew.TotalSeconds > toleranceSeconds)
            return new ReplayResult(Decision.SkewRejected, skew, "skew_exceeded");

        // Build nonce. Prefer explicit header; otherwise hash body+timestamp so
        // identical replays share the same key but different deliveries don't.
        var nonce = TryHeader(headers, nonceHeader) ?? DefaultNonce(body, tsString);
        var expiresAt = ts.AddSeconds(2d * toleranceSeconds);
        var registered = await _store.TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, ct).ConfigureAwait(false);
        if (!registered)
            return new ReplayResult(Decision.ReplayRejected, skew, "nonce_seen");

        return new ReplayResult(Decision.Fresh, skew, null);
    }

    private static string? TryHeader(IReadOnlyDictionary<string, string> headers, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static string DefaultNonce(ReadOnlyMemory<byte> body, string timestamp)
    {
        Span<byte> hash = stackalloc byte[32];
        var seedLength = Encoding.UTF8.GetByteCount(timestamp);
        Span<byte> buffer = seedLength + body.Length <= 4096
            ? stackalloc byte[seedLength + body.Length]
            : new byte[seedLength + body.Length];
        Encoding.UTF8.GetBytes(timestamp, buffer);
        body.Span.CopyTo(buffer[seedLength..]);
        SHA256.HashData(buffer, hash);
        return Convert.ToHexString(hash);
    }
}
