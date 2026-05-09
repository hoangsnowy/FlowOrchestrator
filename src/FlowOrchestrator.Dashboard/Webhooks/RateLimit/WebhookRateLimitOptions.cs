namespace FlowOrchestrator.Dashboard.Webhooks.RateLimit;

/// <summary>
/// Token-bucket parameters for the webhook rate limiter. Disabled by default
/// — set <see cref="PermitsPerSecond"/> &gt; 0 to activate.
/// </summary>
public sealed class WebhookRateLimitOptions
{
    /// <summary>Sustained throughput in permits per second.</summary>
    public double PermitsPerSecond { get; set; }

    /// <summary>Maximum burst size; <see cref="PermitsPerSecond"/> if not specified.</summary>
    public int? BurstSize { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the limiter is keyed on
    /// <c>{flowId}|{clientIp}</c> instead of <c>{flowId}</c>; protects against a
    /// single misbehaving publisher exhausting a flow's budget.
    /// </summary>
    public bool PerIp { get; set; }

    /// <summary><see langword="true"/> when the limiter is configured to do anything.</summary>
    public bool IsEnabled => PermitsPerSecond > 0;
}
