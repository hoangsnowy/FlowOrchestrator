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
    public async Task QueryAsync_returns_total_count_alongside_paged_items()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        for (var i = 0; i < 12; i++)
        {
            await store.WriteAsync(new WebhookRejectionRecord
            {
                FlowId = _flow,
                Reason = "r" + i,
                ReceivedAt = clock.GetUtcNow(),
            });
        }

        // Act
        var page = await store.QueryAsync(new WebhookRejectionQuery(FlowId: _flow, Skip: 5, Take: 4));

        // Assert
        Assert.Equal(12, page.Total);
        Assert.Equal(4, page.Items.Count);
    }

    [Fact]
    public async Task QueryAsync_search_filter_matches_reason_trigger_or_ip()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow, TriggerKey = "github-events", Reason = "rate_limited",
            RemoteIp = "10.0.0.1", ReceivedAt = clock.GetUtcNow(),
        });
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow, TriggerKey = "stripe-events", Reason = "signature_invalid",
            RemoteIp = "192.168.1.1", ReceivedAt = clock.GetUtcNow(),
        });

        // Act + Assert — match by trigger
        var byTrigger = await store.QueryAsync(new WebhookRejectionQuery(Search: "github"));
        Assert.Equal(1, byTrigger.Total);
        Assert.Equal("github-events", byTrigger.Items[0].TriggerKey);

        // by reason
        var byReason = await store.QueryAsync(new WebhookRejectionQuery(Search: "RATE"));
        Assert.Equal(1, byReason.Total);

        // by remote IP
        var byIp = await store.QueryAsync(new WebhookRejectionQuery(Search: "192.168"));
        Assert.Equal(1, byIp.Total);

        // empty search returns everything
        var noFilter = await store.QueryAsync(new WebhookRejectionQuery(Search: null));
        Assert.Equal(2, noFilter.Total);
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

    [Fact]
    public async Task QueryAsync_search_with_null_remote_ip_field_does_not_throw()
    {
        // Arrange — guards the CodeQL "extracted Where-predicate locals" refactor where
        // `searchMatchesIp = r.RemoteIp?.Contains(search ?? string.Empty, ...)` runs for every
        // record, even when `search` is null. A record with RemoteIp = null must not NRE when
        // any of the three extracted match-helpers are computed.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow,
            TriggerKey = "webhook",
            Reason = "rate_limited",
            RemoteIp = null,                  // <-- the critical null field
            ReceivedAt = clock.GetUtcNow(),
            IsAccepted = false,
        });

        // Act + Assert (no throw)
        var nullSearchPage = await store.QueryAsync(new WebhookRejectionQuery(Search: null));
        var nonMatchingSearchPage = await store.QueryAsync(new WebhookRejectionQuery(Search: "no-such-substring"));
        var matchingSearchPage = await store.QueryAsync(new WebhookRejectionQuery(Search: "rate"));

        // Null search: matches everything. Non-matching: matches nothing. Matching reason: 1 row.
        Assert.Equal(1, nullSearchPage.Total);
        Assert.Equal(0, nonMatchingSearchPage.Total);
        Assert.Equal(1, matchingSearchPage.Total);
    }

    [Fact]
    public async Task QueryAsync_combined_filters_search_accepted_flow_all_applied()
    {
        // Arrange — exercises the AND-chain across all three predicate dimensions to lock the
        // post-refactor extracted-locals semantics: matchesAcceptedFilter && matchesRejectedFilter
        // && matchesFlowFilter && matchesReasonFilter && matchesSearchFilter.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookRejectionStore(maxEntries: 50, clock);
        var otherFlow = Guid.Parse("00000000-0000-0000-0000-000000000777");

        // 1) match: matching flow, rejected, "github" trigger contains "git"
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow, TriggerKey = "github-events", Reason = "signature_invalid",
            IsAccepted = false, ReceivedAt = clock.GetUtcNow(),
        });
        // 2) excluded by accepted-only: this row is Accepted but we filter for rejected
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow, TriggerKey = "github-events", Reason = "accepted",
            IsAccepted = true, ReceivedAt = clock.GetUtcNow(),
        });
        // 3) excluded by flow filter: different flow, even though trigger matches
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = otherFlow, TriggerKey = "github-events", Reason = "signature_invalid",
            IsAccepted = false, ReceivedAt = clock.GetUtcNow(),
        });
        // 4) excluded by search filter: matching flow + rejected but trigger doesn't contain "git"
        await store.WriteAsync(new WebhookRejectionRecord
        {
            FlowId = _flow, TriggerKey = "stripe-events", Reason = "signature_invalid",
            IsAccepted = false, ReceivedAt = clock.GetUtcNow(),
        });

        // Act
        var page = await store.QueryAsync(new WebhookRejectionQuery(
            FlowId: _flow,
            IncludeAccepted: false,    // only rejected (excludes #2)
            IncludeRejected: true,
            Search: "git"));           // trigger contains "git" (excludes #4)

        // Assert — only #1 survives all four predicates.
        Assert.Equal(1, page.Total);
        Assert.Equal("github-events", page.Items[0].TriggerKey);
        Assert.False(page.Items[0].IsAccepted);
    }
}
