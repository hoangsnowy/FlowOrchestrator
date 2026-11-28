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
internal sealed class ReplayProtectionGate
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

    /// <summary>Outcome of <c>EvaluateAsync</c>.</summary>
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
    public readonly record struct ReplayResult(Decision Decision, TimeSpan Skew, string? Reason)
    {
        /// <summary>
        /// The replay entry this evaluation recorded (set only for <see cref="Decision.Fresh"/>), so a caller
        /// whose request fails before the run starts can release it again with <see cref="ReleaseAsync"/>.
        /// </summary>
        public WebhookReplayRegistration? Registration { get; init; }
    }

    /// <summary>
    /// Releases a replay entry this gate recorded, so a publisher's retry of a delivery that failed before its
    /// run was started is not rejected as a replay.
    /// </summary>
    /// <param name="registration">The registration returned on the <see cref="Decision.Fresh"/> verdict.</param>
    /// <param name="ct">Cancellation token; compensation callers pass <see cref="CancellationToken.None"/>.</param>
    /// <returns><see langword="true"/> when the entry was still owned by <paramref name="registration"/> and is now gone.</returns>
    /// <remarks>
    /// The store removes the entry only when its owner token matches, so a release can never drop an entry a
    /// different request recorded — a replayer's registration fails, and it never holds the token.
    /// </remarks>
    public ValueTask<bool> ReleaseAsync(WebhookReplayRegistration registration, CancellationToken ct = default)
        => _store.TryForgetAsync(registration.FlowId, registration.TriggerKey, registration.Nonce, registration.RegistrationId, ct);

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
    public ValueTask<ReplayResult> EvaluateAsync(
        Guid flowId,
        string triggerKey,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        int toleranceSeconds,
        string? timestampHeader,
        string? nonceHeader,
        CancellationToken ct = default)
        => EvaluateAsync(
            flowId, triggerKey, headers, body, toleranceSeconds, timestampHeader, nonceHeader,
            signatureVerified: false, authenticatedTimestamp: null, authenticatedNonce: null, ct);

    /// <summary>
    /// Evaluates the request after a signature gate has run. When <paramref name="signatureVerified"/> is
    /// <see langword="true"/>, freshness and the replay key come only from material the signature covered:
    /// unsigned timestamp and nonce headers are ignored, because whoever replays a captured delivery controls
    /// them.
    /// </summary>
    /// <param name="flowId">Resolved flow identifier (scopes the nonce table).</param>
    /// <param name="triggerKey">Resolved trigger key.</param>
    /// <param name="headers">Inbound headers; case-insensitive lookup expected.</param>
    /// <param name="body">Raw body bytes; part of the replay key.</param>
    /// <param name="toleranceSeconds">Maximum allowed skew between client + server clocks.</param>
    /// <param name="timestampHeader">Explicit timestamp header, used only when no signed timestamp exists.</param>
    /// <param name="nonceHeader">Explicit nonce header, used only when no signature was verified.</param>
    /// <param name="signatureVerified">Whether a signature gate verified this request.</param>
    /// <param name="authenticatedTimestamp">The timestamp the verified digest covered, if any.</param>
    /// <param name="authenticatedNonce">The per-delivery token the verified digest covered, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Schemes that sign only the body (GitHub, Bitbucket, …) carry no authenticated timestamp. For them the
    /// replay key is the body hash and its expiry is measured from the server clock, so an identical delivery
    /// is rejected for <c>2 × toleranceSeconds</c>; beyond that window no replay protection is possible
    /// without a signed timestamp. Such a delivery needs no timestamp header at all — these publishers send
    /// none — and an unsigned one, when present, is ignored rather than skew-checked: whoever replays the
    /// delivery chooses it, so it proves nothing about freshness.
    /// </remarks>
    public async ValueTask<ReplayResult> EvaluateAsync(
        Guid flowId,
        string triggerKey,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        int toleranceSeconds,
        string? timestampHeader,
        string? nonceHeader,
        bool signatureVerified,
        string? authenticatedTimestamp,
        string? authenticatedNonce,
        CancellationToken ct = default)
    {
        if (toleranceSeconds <= 0)
            return new ReplayResult(Decision.Disabled, TimeSpan.Zero, null);

        var signedTimestamp = signatureVerified && !string.IsNullOrWhiteSpace(authenticatedTimestamp);

        if (signatureVerified && !signedTimestamp)
        {
            // Body-only signature (GitHub, Shopify, Linear, …): no signed timestamp exists and the publisher
            // sends no timestamp header, so demanding one rejected every genuine delivery (missing_timestamp).
            // Freshness comes from the signed body alone: its hash (or a token the signature covered) is the
            // replay key, held for 2 × tolerance from the server clock.
            var bodyNonce = authenticatedNonce ?? DefaultNonce(body, string.Empty);
            var bodyKeyExpiry = _clock.GetUtcNow().AddSeconds(2d * toleranceSeconds);
            var bodyRegistration = new WebhookReplayRegistration(flowId, triggerKey, bodyNonce, Guid.NewGuid());
            var fresh = await _store.TryRegisterAsync(flowId, triggerKey, bodyNonce, bodyKeyExpiry, bodyRegistration.RegistrationId, ct).ConfigureAwait(false);
            return fresh
                ? new ReplayResult(Decision.Fresh, TimeSpan.Zero, null) { Registration = bodyRegistration }
                : new ReplayResult(Decision.ReplayRejected, TimeSpan.Zero, "nonce_seen");
        }

        // Locate timestamp: the signed one when the signature covered it; otherwise explicit header first,
        // then common standards.
        var tsString = signedTimestamp
            ? authenticatedTimestamp!.Trim()
            : TryHeader(headers, timestampHeader)
              ?? TryHeader(headers, "X-Webhook-Timestamp")
              ?? TryHeader(headers, "X-Slack-Request-Timestamp")
              ?? TryHeader(headers, "X-Zm-Request-Timestamp");
        if (string.IsNullOrWhiteSpace(tsString))
            return new ReplayResult(Decision.TimestampMissing, TimeSpan.Zero, "missing_timestamp");

        if (!long.TryParse(tsString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            || unixSeconds < MinUnixSeconds || unixSeconds > MaxUnixSeconds)
            return new ReplayResult(Decision.TimestampMissing, TimeSpan.Zero, "invalid_timestamp");

        var ts = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var now = _clock.GetUtcNow();
        var skew = (now - ts).Duration();
        if (skew.TotalSeconds > toleranceSeconds)
            return new ReplayResult(Decision.SkewRejected, skew, "skew_exceeded");

        string nonce;
        DateTimeOffset expiresAt;
        if (signatureVerified)
        {
            // Replay key from signed material only. The timestamp is signed here (the body-only case
            // returned above), so the skew check bounds the window and the key may expire relative to it.
            nonce = authenticatedNonce ?? DefaultNonce(body, tsString);
            expiresAt = ts.AddSeconds(2d * toleranceSeconds);
        }
        else
        {
            // No signature: prefer explicit header; otherwise hash body+timestamp so identical replays
            // share the same key but different deliveries don't.
            nonce = TryHeader(headers, nonceHeader) ?? DefaultNonce(body, tsString);
            expiresAt = ts.AddSeconds(2d * toleranceSeconds);
        }

        // An unsigned nonce header is caller-controlled and uncapped; the SQL / PostgreSQL columns are 256
        // characters. Over-long nonces are registered as a fixed-length digest, so they still dedupe.
        nonce = WebhookReplayNonceStorage.ToStorageForm(nonce);
        var registration = new WebhookReplayRegistration(flowId, triggerKey, nonce, Guid.NewGuid());
        var registered = await _store.TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, registration.RegistrationId, ct).ConfigureAwait(false);
        if (!registered)
            return new ReplayResult(Decision.ReplayRejected, skew, "nonce_seen");

        return new ReplayResult(Decision.Fresh, skew, null) { Registration = registration };
    }

    // DateTimeOffset.FromUnixTimeSeconds throws outside this range; reject instead of surfacing a 500.
    private const long MinUnixSeconds = -62_135_596_800;
    private const long MaxUnixSeconds = 253_402_300_799;

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
