namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Declarative description of a webhook signature dialect: which header carries
/// the digest, what algorithm and encoding was used, how the signed payload is
/// composed, and how multi-value (Stripe / Slack) headers should be parsed.
/// </summary>
/// <remarks>
/// Specs are immutable value objects. Built-in partner schemes resolve to a
/// fixed spec via <see cref="PartnerSchemeRegistry"/>; the <see cref="WebhookSignatureScheme.Custom"/>
/// scheme builds a spec at runtime from manifest inputs. Supplying a spec is
/// equivalent to declaring the protocol — the verifier is a single
/// algorithm parameterised by the spec.
/// </remarks>
public sealed record WebhookSignatureSpec
{
    /// <summary>HTTP header that carries the signature value. Empty when the signature lives in the body (Mailgun).</summary>
    public string HeaderName { get; init; } = string.Empty;

    /// <summary>HMAC algorithm. SHA-1 is gated by <c>AllowLegacySha1</c> on the verifier options.</summary>
    public HmacAlgorithm Algorithm { get; init; } = HmacAlgorithm.Sha256;

    /// <summary>Wire-format encoding of the digest.</summary>
    public SignatureEncoding Encoding { get; init; } = SignatureEncoding.HexLower;

    /// <summary>Optional prefix stripped from each candidate value before decoding (e.g. <c>"sha256="</c>).</summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// Delimiter splitting key/value pairs in a multi-value header
    /// (Stripe <c>t=…,v1=…</c> uses <c>","</c>). When <see langword="null"/>
    /// the header value is treated as a single signature.
    /// </summary>
    public string? MultiValueDelimiter { get; init; }

    /// <summary>Separator between key and value inside one segment (e.g. <c>"="</c>).</summary>
    public string? KeyValueSeparator { get; init; }

    /// <summary>Key whose value is the actual signature in a multi-value header (e.g. <c>"v1"</c> for Stripe).</summary>
    public string? SignatureValueKey { get; init; }

    /// <summary>Key whose value is the timestamp in a multi-value header (e.g. <c>"t"</c> for Stripe).</summary>
    public string? TimestampValueKey { get; init; }

    /// <summary>Header that carries the timestamp when it lives outside the signature header (Slack, Zoom).</summary>
    public string? TimestampHeaderName { get; init; }

    /// <summary>Versions accepted from a multi-value header (Stripe defaults to <c>["v1"]</c>).</summary>
    public IReadOnlyList<string>? AcceptedVersions { get; init; }

    /// <summary>Strategy that builds the byte sequence covered by the HMAC.</summary>
    public SignedPayloadStrategy SignedPayloadStrategy { get; init; } = SignedPayloadStrategy.RawBody;

    /// <summary>Delimiter passed to the strategy (e.g. <c>"."</c> for Stripe, <c>":"</c> for Slack).</summary>
    public string? SignedPayloadDelimiter { get; init; }

    /// <summary>Version literal used by colon-delimited strategies (e.g. <c>"v0"</c> for Slack).</summary>
    public string? SignedPayloadVersion { get; init; }

    /// <summary>When <see langword="true"/> the verifier rejects the request if no timestamp is present.</summary>
    public bool RequireTimestamp { get; init; }

    /// <summary>Allow processing more than one signature value from the header (Stripe).</summary>
    public bool AcceptMultipleSignatures { get; init; }

    /// <summary>Strip this prefix from the entire header value before parsing (e.g. <c>"HMAC "</c> for Microsoft Teams).</summary>
    public string? HeaderValuePrefix { get; init; }

    /// <summary>Name of a custom strategy registered via <c>WebhookSecurityOptionsBuilder.AddCustomSignatureStrategy</c>.</summary>
    public string? CustomStrategyName { get; init; }
}
