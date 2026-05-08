using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Signature;

namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Fluent builder over <see cref="WebhookSecurityOptions"/> exposed through
/// <c>FlowDashboardOptions.UseWebhookSecurity(...)</c>. Keeps the configuration
/// surface narrow without forcing operators to mutate dictionaries by hand.
/// </summary>
public sealed class WebhookSecurityOptionsBuilder
{
    private readonly WebhookSecurityOptions _options;

    /// <summary>Wraps an existing options instance.</summary>
    /// <param name="options">Mutable options instance to populate.</param>
    public WebhookSecurityOptionsBuilder(WebhookSecurityOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The underlying options instance.</summary>
    public WebhookSecurityOptions Options => _options;

    /// <summary>Sets <see cref="WebhookSecurityOptions.EnforcementMode"/>.</summary>
    /// <param name="mode">Off / Audit / Enforce.</param>
    public WebhookSecurityOptionsBuilder UseEnforcementMode(WebhookEnforcementMode mode)
    {
        _options.EnforcementMode = mode;
        return this;
    }

    /// <summary>Allows HMAC-SHA1 signatures (default: rejected).</summary>
    public WebhookSecurityOptionsBuilder AllowLegacySha1()
    {
        _options.AllowLegacySha1 = true;
        return this;
    }

    /// <summary>Sets the maximum request body size (in bytes) for webhook receives.</summary>
    /// <param name="bytes">Hard cap; requests exceeding it return 413.</param>
    public WebhookSecurityOptionsBuilder UseMaxBodyBytes(long bytes)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes), "Max body bytes must be positive.");
        _options.MaxBodyBytes = bytes;
        return this;
    }

    /// <summary>
    /// Enables replay protection with the given clock-skew tolerance, optional
    /// default timestamp header, and optional default nonce header.
    /// </summary>
    /// <param name="toleranceSeconds">Maximum allowed skew (defaults to 300 s when omitted).</param>
    /// <param name="timestampHeader">Default timestamp header to read (e.g. <c>X-Webhook-Timestamp</c>).</param>
    /// <param name="nonceHeader">Default nonce / delivery-id header (e.g. <c>X-GitHub-Delivery</c>).</param>
    public WebhookSecurityOptionsBuilder UseReplayProtection(
        int toleranceSeconds = 300,
        string? timestampHeader = null,
        string? nonceHeader = null)
    {
        if (toleranceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceSeconds));
        _options.ReplayToleranceSeconds = toleranceSeconds;
        _options.DefaultTimestampHeader = timestampHeader;
        _options.DefaultNonceHeader = nonceHeader;
        return this;
    }

    /// <summary>
    /// Enables the token-bucket rate limiter with the supplied parameters.
    /// </summary>
    /// <param name="permitsPerSecond">Sustained throughput (must be &gt; 0).</param>
    /// <param name="burstSize">Optional burst capacity. Defaults to <paramref name="permitsPerSecond"/>.</param>
    /// <param name="perIp">When <see langword="true"/>, the limiter is keyed per client IP as well as per flow.</param>
    public WebhookSecurityOptionsBuilder UseRateLimit(double permitsPerSecond, int? burstSize = null, bool perIp = false)
    {
        if (permitsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(permitsPerSecond));
        _options.RateLimit.PermitsPerSecond = permitsPerSecond;
        _options.RateLimit.BurstSize = burstSize;
        _options.RateLimit.PerIp = perIp;
        return this;
    }

    /// <summary>Sets the trusted-proxy depth for <c>X-Forwarded-For</c> parsing.</summary>
    /// <param name="depth">Number of trusted reverse-proxies; 0 disables XFF.</param>
    public WebhookSecurityOptionsBuilder UseForwardedHeaders(int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        _options.ForwardedHeaderDepth = depth;
        return this;
    }

    /// <summary>
    /// Registers a custom signed-payload strategy referenced by
    /// <c>webhookSignedPayloadStrategy = "custom:&lt;name&gt;"</c> in a flow manifest.
    /// </summary>
    /// <param name="name">Strategy name (will be stored on the spec).</param>
    /// <param name="builder">Function that returns the byte sequence covered by the HMAC.</param>
    public WebhookSecurityOptionsBuilder AddCustomSignatureStrategy(
        string name,
        Func<WebhookSignatureContext, byte[]> builder)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Strategy name required.", nameof(name));
        ArgumentNullException.ThrowIfNull(builder);
        _options.CustomSignatureStrategies[name] = builder;
        return this;
    }

    /// <summary>
    /// Registers a named webhook signature scheme that flows can reference via
    /// <c>webhookSignatureScheme = "&lt;name&gt;"</c>. Names are matched
    /// case-insensitively and shadow built-in enum entries on conflict.
    /// </summary>
    /// <param name="name">Scheme name as it will appear in flow manifests.</param>
    /// <param name="spec">Fully-populated signature spec.</param>
    public WebhookSecurityOptionsBuilder RegisterScheme(string name, WebhookSignatureSpec spec)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Scheme name required.", nameof(name));
        ArgumentNullException.ThrowIfNull(spec);
        _options.CustomSchemes[name] = spec;
        return this;
    }
}
