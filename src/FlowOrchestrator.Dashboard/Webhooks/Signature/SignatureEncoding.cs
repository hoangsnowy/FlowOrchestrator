namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Wire-format encoding used to serialise the binary HMAC digest into the
/// signature header value. Decoders normalise whitespace and case before
/// constant-time comparison.
/// </summary>
public enum SignatureEncoding
{
    /// <summary>Lower-case hex (the default for GitHub, Stripe, Slack, Zoom, Linear, Dropbox).</summary>
    HexLower,

    /// <summary>Upper-case hex. Compared case-insensitively after canonicalisation.</summary>
    HexUpper,

    /// <summary>Standard base64 with padding (Shopify, Twilio, Square, Microsoft Teams).</summary>
    Base64,

    /// <summary>URL-safe base64; decoder tolerates missing <c>=</c> padding.</summary>
    Base64Url,
}
