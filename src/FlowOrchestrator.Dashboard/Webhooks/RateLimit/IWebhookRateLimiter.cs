namespace FlowOrchestrator.Dashboard.Webhooks.RateLimit;

/// <summary>
/// Per-key rate-limit decision contract. The default
/// <see cref="TokenBucketWebhookRateLimiter"/> wraps
/// <c>System.Threading.RateLimiting.PartitionedRateLimiter</c>; a future
/// distributed implementation (Redis / SQL) plugs in without changing the
/// pipeline shape.
/// </summary>
public interface IWebhookRateLimiter
{
    /// <summary>
    /// Asks the limiter whether one permit may be consumed for the given
    /// <paramref name="key"/>. Returns the verdict + a non-zero retry-after
    /// hint when rejected.
    /// </summary>
    /// <param name="key">Composite key (e.g. <c>"flowId|clientIp"</c>).</param>
    /// <param name="options">Per-flow limit parameters.</param>
    RateLimitDecision TryAcquire(string key, WebhookRateLimitOptions options);
}

/// <summary>Verdict from an <see cref="IWebhookRateLimiter"/> call.</summary>
/// <param name="Allowed">True when a permit was acquired.</param>
/// <param name="RetryAfter">Hint for the <c>Retry-After</c> header on rejection.</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
