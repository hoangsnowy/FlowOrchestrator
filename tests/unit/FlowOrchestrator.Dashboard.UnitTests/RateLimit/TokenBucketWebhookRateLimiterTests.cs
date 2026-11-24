using FlowOrchestrator.Dashboard.Webhooks.RateLimit;

namespace FlowOrchestrator.Dashboard.UnitTests.RateLimit;

/// <summary>Tests for the token-bucket webhook rate limiter.</summary>
public sealed class TokenBucketWebhookRateLimiterTests
{
    [Fact]
    public void Disabled_options_always_allow()
    {
        // Arrange
        using var limiter = new TokenBucketWebhookRateLimiter();
        var opts = new WebhookRateLimitOptions(); // PermitsPerSecond = 0

        // Act
        var result = limiter.TryAcquire("flow-1", opts);

        // Assert
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Burst_allows_first_n_then_rejects()
    {
        // Arrange
        using var limiter = new TokenBucketWebhookRateLimiter();
        var opts = new WebhookRateLimitOptions { PermitsPerSecond = 1, BurstSize = 3 };

        // Act
        var first = limiter.TryAcquire("flow-1", opts);
        var second = limiter.TryAcquire("flow-1", opts);
        var third = limiter.TryAcquire("flow-1", opts);
        var fourth = limiter.TryAcquire("flow-1", opts);

        // Assert
        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.True(third.Allowed);
        Assert.False(fourth.Allowed);
    }

    [Fact]
    public void Independent_keys_have_independent_buckets()
    {
        // Arrange
        using var limiter = new TokenBucketWebhookRateLimiter();
        var opts = new WebhookRateLimitOptions { PermitsPerSecond = 1, BurstSize = 1 };

        // Act
        var a1 = limiter.TryAcquire("flow-A", opts);
        var b1 = limiter.TryAcquire("flow-B", opts);
        var a2 = limiter.TryAcquire("flow-A", opts);

        // Assert
        Assert.True(a1.Allowed);
        Assert.True(b1.Allowed);
        Assert.False(a2.Allowed);
    }

    [Fact]
    public void Bucket_follows_changed_options_for_the_same_key()
    {
        // Arrange — the key was first seen with a 1-token burst, then its trigger asks for 100.
        using var limiter = new TokenBucketWebhookRateLimiter();
        var slow = new WebhookRateLimitOptions { PermitsPerSecond = 1, BurstSize = 1 };
        var fast = new WebhookRateLimitOptions { PermitsPerSecond = 100, BurstSize = 100 };
        limiter.TryAcquire("flow-1|webhook", slow);

        // Act
        var results = Enumerable.Range(0, 50).Select(_ => limiter.TryAcquire("flow-1|webhook", fast).Allowed).ToArray();

        // Assert
        Assert.All(results, Assert.True);
    }
}
