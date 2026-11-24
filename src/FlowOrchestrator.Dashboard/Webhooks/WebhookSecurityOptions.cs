using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Root configuration for the webhook hardening pipeline. Lives under
/// <see cref="FlowDashboardOptions.WebhookSecurity"/>. Since 2.0 the default mode is
/// <see cref="WebhookEnforcementMode.Enforce"/>: every gate a flow's trigger declares (signature
/// scheme + key, IP lists, rate limit, replay tolerance) rejects violating requests. A flow that declares
/// no gate inputs is unaffected.
/// </summary>
public sealed class WebhookSecurityOptions
{
    /// <summary>
    /// How rejecting gates behave. <see cref="WebhookEnforcementMode.Enforce"/> by default.
    /// </summary>
    /// <remarks>
    /// 1.x defaulted to <see cref="WebhookEnforcementMode.Off"/>, which accepted every request before any gate
    /// ran — a flow that declared an HMAC key and signature scheme was still not verified. Choose
    /// <see cref="WebhookEnforcementMode.Audit"/> explicitly to record violations without rejecting while a
    /// publisher is being onboarded.
    /// </remarks>
    public WebhookEnforcementMode EnforcementMode { get; set; } = WebhookEnforcementMode.Enforce;

    /// <summary>
    /// Allow HMAC-SHA1 signatures (legacy GitHub <c>X-Hub-Signature</c>). SHA-1
    /// is rejected by default because publishers have migrated to SHA-256.
    /// </summary>
    public bool AllowLegacySha1 { get; set; }

    /// <summary>
    /// Hard cap on request body bytes the dashboard will buffer before
    /// rejecting with HTTP 413. Defaults to 1 MiB (most webhook payloads are
    /// well under 100 KB; a 1 MiB cap leaves room for outliers like Shopify
    /// product feeds).
    /// </summary>
    public long MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum allowed clock skew between the publisher's signed timestamp and
    /// server time, in seconds. <c>0</c> disables replay protection (default).
    /// </summary>
    public int ReplayToleranceSeconds { get; set; }

    /// <summary>Default header name read for the publisher timestamp when the trigger does not specify one.</summary>
    public string? DefaultTimestampHeader { get; set; }

    /// <summary>Default header name read for the publisher nonce / delivery-id when the trigger does not specify one.</summary>
    public string? DefaultNonceHeader { get; set; }

    /// <summary>Default token-bucket rate-limit applied when a flow does not override.</summary>
    public WebhookRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// Number of trusted reverse proxies in front of the host (load balancer, ingress, CDN …), used to read
    /// the webhook caller's address from <c>X-Forwarded-For</c> for IP allow / deny lists and per-IP rate
    /// limits. <c>0</c> (default) ignores the header and uses the socket address.
    /// </summary>
    /// <remarks>
    /// Each proxy appends the address it received the request from, so behind N proxies the client is the
    /// entry N positions from the right; entries further left were sent by the caller and are not trusted.
    /// With fewer than N entries the socket address is used. Set it to exactly the number of proxies you
    /// operate: one too many lets a caller choose its own address, one too few reports a proxy's address.
    /// </remarks>
    public int ForwardedHeaderDepth { get; set; }

    /// <summary>
    /// User-registered custom signed-payload strategies referenced by
    /// <see cref="WebhookSignatureSpec.CustomStrategyName"/>.
    /// </summary>
    /// <remarks>
    /// Populated through <see cref="WebhookSecurityOptionsBuilder.AddCustomSignatureStrategy"/>.
    /// Unused for the built-in partner schemes.
    /// </remarks>
    public IDictionary<string, Func<WebhookSignatureContext, byte[]>> CustomSignatureStrategies { get; }
        = new Dictionary<string, Func<WebhookSignatureContext, byte[]>>(StringComparer.Ordinal);

    /// <summary>
    /// Operator-supplied scheme registry overlay. Entries shadow built-ins on
    /// <see cref="PartnerSchemeRegistry"/> and add new named schemes that flows
    /// can reference via <c>webhookSignatureScheme = "&lt;name&gt;"</c>.
    /// </summary>
    public IDictionary<string, WebhookSignatureSpec> CustomSchemes { get; }
        = new Dictionary<string, WebhookSignatureSpec>(StringComparer.OrdinalIgnoreCase);
}
