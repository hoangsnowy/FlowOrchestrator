using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace FlowOrchestrator.Dashboard.Webhooks.RateLimit;

/// <summary>
/// In-process token-bucket implementation of <see cref="IWebhookRateLimiter"/>.
/// One <see cref="TokenBucketRateLimiter"/> per key, lazily created and cached
/// in a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Per-key isolation guarantees a noisy publisher cannot drain another trigger's
/// budget. A key's bucket follows the options it is called with: when they
/// change (a different rate or burst for the same key), the bucket is rebuilt
/// with the new parameters instead of keeping the ones it was first created
/// with. The dictionary grows monotonically with the active key set; bound it
/// with the <c>PerIp</c> opt-in only when that's actually needed.
/// </remarks>
public sealed class TokenBucketWebhookRateLimiter : IWebhookRateLimiter, IDisposable
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private bool _disposed;

    /// <inheritdoc/>
    public RateLimitDecision TryAcquire(string key, WebhookRateLimitOptions options)
    {
        if (!options.IsEnabled || _disposed)
            return new RateLimitDecision(true, TimeSpan.Zero);

        var (tokenLimit, tokensPerPeriod) = Parameters(options);

        // Two attempts: a concurrent parameter change may dispose the bucket this call just read.
        for (var attempt = 0; ; attempt++)
        {
            var bucket = _buckets.GetOrAdd(key, static (_, p) => Bucket.Create(p.tokenLimit, p.tokensPerPeriod), (tokenLimit, tokensPerPeriod));
            if (bucket.TokenLimit != tokenLimit || bucket.TokensPerPeriod != tokensPerPeriod)
            {
                var rebuilt = Bucket.Create(tokenLimit, tokensPerPeriod);
                if (_buckets.TryUpdate(key, rebuilt, bucket))
                {
                    DisposeQuietly(bucket.Limiter);
                    bucket = rebuilt;
                }
                else
                {
                    DisposeQuietly(rebuilt.Limiter);
                    continue;   // someone else replaced it; re-read
                }
            }

            RateLimitLease lease;
            try
            {
                lease = bucket.Limiter.AttemptAcquire(permitCount: 1);
            }
            catch (ObjectDisposedException) when (attempt == 0 && !_disposed)
            {
                continue;
            }

            using (lease)
            {
                if (lease.IsAcquired)
                    return new RateLimitDecision(true, TimeSpan.Zero);

                var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
                    ? retryAfter
                    : TimeSpan.FromSeconds(1);
                return new RateLimitDecision(false, retry);
            }
        }
    }

    private static (int TokenLimit, int TokensPerPeriod) Parameters(WebhookRateLimitOptions options)
    {
        var tokensPerPeriod = Math.Max(1, (int)Math.Ceiling(options.PermitsPerSecond));
        return (options.BurstSize ?? tokensPerPeriod, tokensPerPeriod);
    }

    private static void DisposeQuietly(TokenBucketRateLimiter limiter)
    {
        // Dispose path: any exception (incl. OCE) must not break the request or shutdown.
        try { limiter.Dispose(); } catch (Exception ex) when (ex is not null) { /* swallow */ }
    }

    /// <summary>Disposes every internal limiter; further calls are no-ops.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var bucket in _buckets.Values)
        {
            DisposeQuietly(bucket.Limiter);
        }
        _buckets.Clear();
    }

    /// <summary>A limiter plus the parameters it was built with, so a parameter change is detectable.</summary>
    private sealed record Bucket(int TokenLimit, int TokensPerPeriod, TokenBucketRateLimiter Limiter)
    {
        public static Bucket Create(int tokenLimit, int tokensPerPeriod) => new(
            tokenLimit,
            tokensPerPeriod,
            new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                QueueLimit = 0,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = tokensPerPeriod,
                AutoReplenishment = true,
            }));
    }
}
