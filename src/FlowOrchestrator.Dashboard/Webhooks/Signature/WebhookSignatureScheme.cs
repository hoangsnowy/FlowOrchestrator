namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Built-in webhook signature schemes recognised by the dashboard out of the box.
/// Each enum value resolves to a fully-populated <see cref="WebhookSignatureSpec"/>
/// in <see cref="PartnerSchemeRegistry"/>; choose <see cref="Custom"/> to drive the
/// verifier from manifest fields directly.
/// </summary>
public enum WebhookSignatureScheme
{
    /// <summary>No partner scheme selected. Treated as "verifier disabled" unless paired with a custom spec.</summary>
    None = 0,

    /// <summary>Catch-all generic HMAC: SHA-256 + lower-case hex + <c>sha256=</c> prefix + raw-body strategy.</summary>
    Generic,

    /// <summary>GitHub <c>X-Hub-Signature-256</c>: SHA-256 hex with <c>sha256=</c> prefix; raw body.</summary>
    GitHub,

    /// <summary>Legacy GitHub <c>X-Hub-Signature</c>: SHA-1 hex with <c>sha1=</c> prefix; gated by <c>AllowLegacySha1</c>.</summary>
    GitHubLegacy,

    /// <summary>Bitbucket Server / Bitbucket Cloud — same shape as GitHub modern.</summary>
    Bitbucket,

    /// <summary>Stripe <c>Stripe-Signature</c>: multi-value header <c>t=…,v1=…</c> over <c>{ts}.{body}</c>.</summary>
    Stripe,

    /// <summary>Slack <c>X-Slack-Signature</c>: <c>v0=…</c> over <c>v0:{ts}:{body}</c>.</summary>
    Slack,

    /// <summary>Shopify <c>X-Shopify-Hmac-SHA256</c>: SHA-256 base64 over raw body, no prefix.</summary>
    Shopify,

    /// <summary>Twilio <c>X-Twilio-Signature</c>: SHA-1 base64 over <c>{url}{sortedFormParams}</c>.</summary>
    Twilio,

    /// <summary>Square <c>X-Square-HmacSha256-Signature</c>: SHA-256 base64 over <c>{url}{body}</c>.</summary>
    Square,

    /// <summary>Zoom <c>X-Zm-Signature</c>: <c>v0=…</c> hex over <c>v0:{ts}:{body}</c>.</summary>
    Zoom,

    /// <summary>Linear <c>Linear-Signature</c>: SHA-256 hex over raw body, no prefix.</summary>
    Linear,

    /// <summary>Dropbox <c>X-Dropbox-Signature</c>: SHA-256 hex over raw body, no prefix.</summary>
    Dropbox,

    /// <summary>Mailgun: signature/timestamp/token live in the JSON body; HMAC covers <c>{ts}{token}</c>.</summary>
    Mailgun,

    /// <summary>Microsoft Teams Outgoing Webhook: <c>Authorization: HMAC &lt;base64&gt;</c>; SHA-256 over raw body.</summary>
    MicrosoftTeams,

    /// <summary>Atlassian webhook: <c>X-Hub-Signature</c> with <c>sha256=</c> prefix over raw body.</summary>
    Atlassian,

    /// <summary>Calendly: same shape as Stripe — multi-value <c>t=,v1=</c> over <c>{ts}.{body}</c>.</summary>
    Calendly,

    /// <summary>Fully manifest-driven verifier; every aspect of the spec is read from flow inputs.</summary>
    Custom,
}
