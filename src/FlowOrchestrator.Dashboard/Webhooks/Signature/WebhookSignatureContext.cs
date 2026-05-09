namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Per-request input to <see cref="IWebhookSignatureVerifier"/>. Carries the raw
/// body bytes (signatures cover bytes, never the parsed object), the inbound
/// header dictionary, the absolute request URL (needed by Twilio / Square),
/// optional form-field map (Twilio), the spec selected for the flow, and the
/// current + previous HMAC keys for zero-downtime rotation.
/// </summary>
public sealed record WebhookSignatureContext
{
    /// <summary>Raw HTTP body. Empty <see cref="ReadOnlyMemory{T}"/> for empty payloads.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Inbound HTTP header collection, case-insensitive lookup.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Absolute request URL including scheme, host, port (if non-default) and path. Required by URL-binding strategies.</summary>
    public string? AbsoluteUrl { get; init; }

    /// <summary>Form-encoded fields parsed from the body, populated only when the strategy needs them (Twilio).</summary>
    public IReadOnlyDictionary<string, string>? FormFields { get; init; }

    /// <summary>Active signature spec for this flow.</summary>
    public required WebhookSignatureSpec Spec { get; init; }

    /// <summary>Current HMAC key (UTF-8 secret string).</summary>
    public required string HmacKey { get; init; }

    /// <summary>Optional previous HMAC key — verified after the current key for rotation windows.</summary>
    public string? HmacKeyPrevious { get; init; }

    /// <summary>Operator-controlled flag to allow SHA-1 algorithms (legacy GitHub).</summary>
    public bool AllowLegacySha1 { get; init; }
}
