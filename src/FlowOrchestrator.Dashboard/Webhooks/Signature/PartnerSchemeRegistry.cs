using System.Collections.Frozen;

namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Static lookup that resolves a <see cref="WebhookSignatureScheme"/> to the
/// fully-populated <see cref="WebhookSignatureSpec"/> implementing that
/// publisher's wire format.
/// </summary>
/// <remarks>
/// Specs are derived from the public webhook security documentation of each
/// publisher (GitHub, Stripe, Slack, Shopify, Twilio, Square, Zoom, Linear,
/// Dropbox, Mailgun, Microsoft Teams, Atlassian, Calendly, Bitbucket).
/// Custom and Generic do not have a fixed spec — Generic falls back to a sane
/// default and Custom is built from manifest inputs.
/// </remarks>
public static class PartnerSchemeRegistry
{
    private static readonly FrozenDictionary<WebhookSignatureScheme, WebhookSignatureSpec> _specs =
        BuildSpecs().ToFrozenDictionary();

    /// <summary>
    /// Returns the spec for a built-in <paramref name="scheme"/>, or
    /// <see langword="null"/> for <see cref="WebhookSignatureScheme.None"/>
    /// and <see cref="WebhookSignatureScheme.Custom"/>.
    /// </summary>
    /// <param name="scheme">Built-in scheme enum value.</param>
    public static WebhookSignatureSpec? TryGet(WebhookSignatureScheme scheme) =>
        _specs.TryGetValue(scheme, out var spec) ? spec : null;

    /// <summary>Returns every built-in scheme that has a registered spec.</summary>
    public static IReadOnlyCollection<WebhookSignatureScheme> KnownSchemes => _specs.Keys;

    private static IEnumerable<KeyValuePair<WebhookSignatureScheme, WebhookSignatureSpec>> BuildSpecs()
    {
        yield return Pair(WebhookSignatureScheme.Generic, new WebhookSignatureSpec
        {
            HeaderName = "X-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "sha256=",
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.GitHub, new WebhookSignatureSpec
        {
            HeaderName = "X-Hub-Signature-256",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "sha256=",
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.GitHubLegacy, new WebhookSignatureSpec
        {
            HeaderName = "X-Hub-Signature",
            Algorithm = HmacAlgorithm.Sha1,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "sha1=",
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Bitbucket, new WebhookSignatureSpec
        {
            HeaderName = "X-Hub-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "sha256=",
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Stripe, new WebhookSignatureSpec
        {
            HeaderName = "Stripe-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            MultiValueDelimiter = ",",
            KeyValueSeparator = "=",
            SignatureValueKey = "v1",
            TimestampValueKey = "t",
            AcceptedVersions = new[] { "v1" },
            SignedPayloadStrategy = SignedPayloadStrategy.TimestampDotBody,
            SignedPayloadDelimiter = ".",
            RequireTimestamp = true,
            AcceptMultipleSignatures = true,
        });

        yield return Pair(WebhookSignatureScheme.Slack, new WebhookSignatureSpec
        {
            HeaderName = "X-Slack-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "v0=",
            TimestampHeaderName = "X-Slack-Request-Timestamp",
            SignedPayloadStrategy = SignedPayloadStrategy.ColonDelimited,
            SignedPayloadDelimiter = ":",
            SignedPayloadVersion = "v0",
            RequireTimestamp = true,
        });

        yield return Pair(WebhookSignatureScheme.Shopify, new WebhookSignatureSpec
        {
            HeaderName = "X-Shopify-Hmac-SHA256",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Twilio, new WebhookSignatureSpec
        {
            HeaderName = "X-Twilio-Signature",
            Algorithm = HmacAlgorithm.Sha1,
            Encoding = SignatureEncoding.Base64,
            SignedPayloadStrategy = SignedPayloadStrategy.UrlPlusSortedForm,
        });

        yield return Pair(WebhookSignatureScheme.Square, new WebhookSignatureSpec
        {
            HeaderName = "X-Square-HmacSha256-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64,
            SignedPayloadStrategy = SignedPayloadStrategy.UrlPlusBody,
        });

        yield return Pair(WebhookSignatureScheme.Zoom, new WebhookSignatureSpec
        {
            HeaderName = "X-Zm-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "v0=",
            TimestampHeaderName = "X-Zm-Request-Timestamp",
            SignedPayloadStrategy = SignedPayloadStrategy.ColonDelimited,
            SignedPayloadDelimiter = ":",
            SignedPayloadVersion = "v0",
            RequireTimestamp = true,
        });

        yield return Pair(WebhookSignatureScheme.Linear, new WebhookSignatureSpec
        {
            HeaderName = "Linear-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Dropbox, new WebhookSignatureSpec
        {
            HeaderName = "X-Dropbox-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        // Mailgun is unusual: signature lives in the body, header is empty.
        // The verifier handles this by reading signature/timestamp/token out of
        // the parsed JSON before HMACing {ts}{token}.
        yield return Pair(WebhookSignatureScheme.Mailgun, new WebhookSignatureSpec
        {
            HeaderName = string.Empty,
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            SignedPayloadStrategy = SignedPayloadStrategy.TimestampPlusToken,
            RequireTimestamp = true,
        });

        yield return Pair(WebhookSignatureScheme.MicrosoftTeams, new WebhookSignatureSpec
        {
            HeaderName = "Authorization",
            HeaderValuePrefix = "HMAC ",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64,
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Atlassian, new WebhookSignatureSpec
        {
            HeaderName = "X-Hub-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            Prefix = "sha256=",
            SignedPayloadStrategy = SignedPayloadStrategy.RawBody,
        });

        yield return Pair(WebhookSignatureScheme.Calendly, new WebhookSignatureSpec
        {
            HeaderName = "Calendly-Webhook-Signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.HexLower,
            MultiValueDelimiter = ",",
            KeyValueSeparator = "=",
            SignatureValueKey = "v1",
            TimestampValueKey = "t",
            AcceptedVersions = new[] { "v1" },
            SignedPayloadStrategy = SignedPayloadStrategy.TimestampDotBody,
            SignedPayloadDelimiter = ".",
            RequireTimestamp = true,
            AcceptMultipleSignatures = true,
        });

        static KeyValuePair<WebhookSignatureScheme, WebhookSignatureSpec> Pair(
            WebhookSignatureScheme scheme, WebhookSignatureSpec spec) => new(scheme, spec);
    }
}
