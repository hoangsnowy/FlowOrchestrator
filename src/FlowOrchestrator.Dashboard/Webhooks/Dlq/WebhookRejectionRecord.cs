namespace FlowOrchestrator.Dashboard.Webhooks.Dlq;

/// <summary>
/// One row in the webhook DLQ. Persisted by <see cref="IWebhookRejectionStore"/>
/// when the security pipeline rejects a request, plus a slim "accepted" log for
/// the dashboard recent-receives view.
/// </summary>
public sealed record WebhookRejectionRecord
{
    /// <summary>Auto-generated row identifier. Set by the store.</summary>
    public long Id { get; init; }

    /// <summary>Resolved flow identifier; <see langword="null"/> when the route did not match a flow.</summary>
    public Guid? FlowId { get; init; }

    /// <summary>Trigger key the request was matched to; <see langword="null"/> when unknown.</summary>
    public string? TriggerKey { get; init; }

    /// <summary>UTC instant the request hit the dashboard.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Best-effort client IP after XFF parsing.</summary>
    public string? RemoteIp { get; init; }

    /// <summary>Failure category (<c>signature_invalid</c>, <c>replay</c>, <c>rate_limited</c>, ...). Empty for accepted entries.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>HTTP status the dashboard returned to the publisher.</summary>
    public int StatusCode { get; init; }

    /// <summary>Total body bytes the publisher sent (or attempted to send before the cap).</summary>
    public int BodyBytes { get; init; }

    /// <summary>Up to 4 KiB of the body, redacted of bearer-style headers, stored as JSON. Plain UTF-8 string when not JSON.</summary>
    public string? BodyTruncated { get; init; }

    /// <summary>Filtered request headers JSON (sensitive headers stripped).</summary>
    public string? HeadersJson { get; init; }

    /// <summary>Selected signature scheme name when known, for triage filtering.</summary>
    public string? Scheme { get; init; }

    /// <summary>Wall-clock pipeline processing time in milliseconds.</summary>
    public double? ProcessingMs { get; init; }

    /// <summary><see langword="true"/> when this row represents an accepted delivery (recent-receives log) rather than a rejection.</summary>
    public bool IsAccepted { get; init; }
}
