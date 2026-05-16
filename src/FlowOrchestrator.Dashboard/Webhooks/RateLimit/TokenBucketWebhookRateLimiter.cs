using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace FlowOrchestrator.Dashboard.Webhooks.RateLimit;

/// <summary>
/// In-process token-bucket implementation of <see cref="IWebhookRateLimiter"/>.
/// One <see cref="TokenBucketRateLimiter"/> per key, lazily created and cached
/// in a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Buckets share their parameters across flows configured the same way.
/// Per-key isolation guarantees a noisy publisher cannot drain another flow's
/// budget. The dictionary grows monotonically with the active key set; bound
/// it with the <c>PerIp</c> opt-in only when that's actually needed.
/// </remarks>
public sealed class TokenBucketWebhookRateLimiter : IWebhookRateLimiter, IDisposable
{
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _buckets = new();
    private bool _disposed;

    /// <inheritdoc/>
    public RateLimitDecision TryAcquire(string key, WebhookRateLimitOptions options)
    {
        if (!options.IsEnabled || _disposed)
            return new RateLimitDecision(true, TimeSpan.Zero);

        var bucket = _buckets.GetOrAdd(key, _ => CreateBucket(options));
        var lease = bucket.AttemptAcquire(permitCount: 1);
        if (lease.IsAcquired)
            return new RateLimitDecision(true, TimeSpan.Zero);

        var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
            ? retryAfter
            : TimeSpan.FromSeconds(1);
        return new RateLimitDecision(false, retry);
    }

    private static TokenBucketRateLimiter CreateBucket(WebhookRateLimitOptions options)
    {
        var burst = options.BurstSize ?? Math.Max(1, (int)Math.Ceiling(options.PermitsPerSecond));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = Math.Max(1, (int)Math.Ceiling(options.PermitsPerSecond)),
            AutoReplenishment = true,
        });
    }

    /// <summary>Disposes every internal limiter; further calls are no-ops.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var bucket in _buckets.Values)
        {
            // Dispose path: any exception (incl. OCE) must not break shutdown.
            try { bucket.Dispose(); } catch (Exception ex) when (ex is not null) { /* swallow shutdown noise */ }
        }
        _buckets.Clear();
    }
}
