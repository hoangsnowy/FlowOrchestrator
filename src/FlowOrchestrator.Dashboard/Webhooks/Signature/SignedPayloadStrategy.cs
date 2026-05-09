namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Strategy for assembling the byte sequence that the HMAC digest covers.
/// Different webhook publishers sign different combinations of timestamp,
/// version marker, request URL, form fields and body, so the verifier needs
/// pluggable composition rather than a single hard-coded shape.
/// </summary>
public enum SignedPayloadStrategy
{
    /// <summary>Sign raw request body bytes (GitHub, Shopify, Linear, Dropbox).</summary>
    RawBody,

    /// <summary>Sign <c>{timestamp}.{body}</c>; uses the configured payload delimiter (default '.'). Stripe / Calendly.</summary>
    TimestampDotBody,

    /// <summary>Sign <c>{version}:{timestamp}:{body}</c>; uses the configured delimiter (default ':'). Slack / Zoom.</summary>
    ColonDelimited,

    /// <summary>Sign <c>{absoluteUrl}{sortedFormFieldConcatenation}</c>. Twilio.</summary>
    UrlPlusSortedForm,

    /// <summary>Sign <c>{absoluteUrl}{body}</c>. Square.</summary>
    UrlPlusBody,

    /// <summary>Sign <c>{timestamp}{token}</c>; signature itself lives in the body, not the header. Mailgun.</summary>
    TimestampPlusToken,

    /// <summary>User-supplied <see cref="System.Func{T, TResult}"/> registered through the <c>WebhookSecurityOptionsBuilder</c>.</summary>
    Custom,
}
