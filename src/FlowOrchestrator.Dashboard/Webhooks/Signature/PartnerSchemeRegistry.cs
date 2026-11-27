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
/// Dropbox, Mailgun, Microsoft Teams, Atlassian, Calendly, Bitbucket) and from the Standard Webhooks
/// specification.
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
            // "Base64-decode the security token provided by Teams when you registered the Outgoing Webhook
            // and use the resulting bytes as the HMAC key." — Microsoft Learn, "Create an Outgoing Webhook",
            // https://learn.microsoft.com/microsoftteams/platform/webhooks-and-connectors/how-to/add-outgoing-webhook
            // (reference sample: Convert.FromBase64String(signingKey)).
            KeyEncoding = HmacKeyEncoding.Base64,
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

        // Standard Webhooks — https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md
        // (used by Svix, Clerk, Resend, Lemon Squeezy, …). From the spec:
        //  • headers "webhook-id", "webhook-timestamp" ("integer unix timestamp (seconds since epoch)") and
        //    "webhook-signature" ("a space delimited list of signatures");
        //  • symmetric signed content "msg_id.timestamp.payload" — "the message's: ID, timestamp and body are
        //    concatenated (delimited by full-stops) and then signed";
        //  • signature "v1, followed by a comma (,), followed by the base64 encoded signature"; other versions
        //    (asymmetric v2a, deprecated v1a) are skipped — only v1 is HMAC, and a valid digest under another
        //    version label must not be accepted (the reference libraries reject it too);
        //  • key "the base64 decoded part after whsec_" (HmacKeyEncoding.Base64 strips the optional prefix);
        //  • "use a constant time comparison function" (every candidate goes through FixedTimeEquals) and verify
        //    the timestamp is "within some allowable tolerance" — the signed timestamp and webhook-id are reported
        //    as authenticated, so UseReplayProtection(...) checks the skew and uses webhook-id as the replay key,
        //    in line with the spec's "use the webhook-id header as an idempotency key".
        yield return Pair(WebhookSignatureScheme.StandardWebhooks, new WebhookSignatureSpec
        {
            HeaderName = "webhook-signature",
            Algorithm = HmacAlgorithm.Sha256,
            Encoding = SignatureEncoding.Base64,
            KeyEncoding = HmacKeyEncoding.Base64,
            MultiValueDelimiter = " ",
            KeyValueSeparator = ",",
            SignatureValueKey = "v1",
            AcceptedVersions = new[] { "v1" },
            TimestampHeaderName = "webhook-timestamp",
            MessageIdHeaderName = "webhook-id",
            SignedPayloadStrategy = SignedPayloadStrategy.MessageIdTimestampBody,
            SignedPayloadDelimiter = ".",
            RequireTimestamp = true,
            AcceptMultipleSignatures = true,
            IgnoreMalformedEntries = true,
        });

        static KeyValuePair<WebhookSignatureScheme, WebhookSignatureSpec> Pair(
            WebhookSignatureScheme scheme, WebhookSignatureSpec spec) => new(scheme, spec);
    }
}
