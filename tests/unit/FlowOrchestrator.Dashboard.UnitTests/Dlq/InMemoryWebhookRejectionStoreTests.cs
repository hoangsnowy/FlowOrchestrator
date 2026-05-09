using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks.Dlq;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.Dashboard.UnitTests.Dlq;

/// <summary>Tests the in-memory ring-buffer rejection store.</summary>
public sealed class InMemoryWebhookRejectionStoreTests
{
    private static readonly Guid _flow = Guid.Parse("00000000-0000-0000-0000-000000000099");

    [Fact]
    public async Task Write_then_query_returns_inserted_record()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow,
            TriggerKey = "webhook",
            Reason = "signature_invalid",
            ReceivedAt = clock.GetUtcNow(),
            StatusCode = 401,
            BodyBytes = 12,
            IsAccepted = false,
        });

        // Act
        var items = await store.QueryRecentAsync(_flow, reason: null, includeAccepted: true, skip: 0, take: 10);

        // Assert
        Assert.Single(items);
        Assert.Equal("signature_invalid", items[0].Reason);
        Assert.True(items[0].Id > 0);
    }

    [Fact]
    public async Task Ring_buffer_drops_oldest_when_full()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 3, clock);
        for (var i = 0; i < 5; i++)
        {
            await store.WriteAsync(new WebhookRejectionRecord
            {
                FlowId = _flow,
                Reason = "r" + i,
                ReceivedAt = clock.GetUtcNow(),
            });
        }

        // Act
        var items = await store.QueryRecentAsync(_flow, reason: null, includeAccepted: true, skip: 0, take: 10);

        // Assert — most-recent first, 3 kept
        Assert.Equal(3, items.Count);
        Assert.Equal("r4", items[0].Reason);
        Assert.Equal("r3", items[1].Reason);
        Assert.Equal("r2", items[2].Reason);
    }

    [Fact]
    public async Task Reason_filter_is_case_insensitive()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "SIGNATURE_INVALID", FlowId = _flow, ReceivedAt = clock.GetUtcNow() });
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "rate_limited", FlowId = _flow, ReceivedAt = clock.GetUtcNow() });

        // Act
        var sigItems = await store.QueryRecentAsync(_flow, reason: "signature_invalid", includeAccepted: true, skip: 0, take: 10);

        // Assert
        Assert.Single(sigItems);
    }

    [Fact]
    public async Task Counts_by_reason_filters_window()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        // Two entries inside window
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "replay", ReceivedAt = clock.GetUtcNow() });
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "replay", ReceivedAt = clock.GetUtcNow() });
        // One stale entry written and then we advance the clock past the window
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "rate_limited", ReceivedAt = clock.GetUtcNow().AddHours(-25) });

        // Act
        var counts = await store.CountsByReasonAsync(TimeSpan.FromHours(24));

        // Assert
        Assert.Equal(2, counts["replay"]);
        Assert.False(counts.ContainsKey("rate_limited"));
    }

    [Fact]
    public async Task Include_accepted_false_excludes_accepted_rows()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "ok", IsAccepted = true, ReceivedAt = clock.GetUtcNow() });
        await store.WriteAsync(new WebhookRejectionRecord { Reason = "signature_invalid", IsAccepted = false, ReceivedAt = clock.GetUtcNow() });

        // Act
        var rejected = await store.QueryRecentAsync(flowId: null, reason: null, includeAccepted: false, skip: 0, take: 50);

        // Assert
        Assert.Single(rejected);
        Assert.False(rejected[0].IsAccepted);
    }
}
