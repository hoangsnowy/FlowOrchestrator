using System.Globalization;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.Dashboard.UnitTests.Replay;

/// <summary>Tests <see cref="ReplayProtectionGate"/> skew + nonce dedup behaviour.</summary>
public sealed class ReplayProtectionGateTests
{
    private static readonly Guid _flow = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string _trigger = "webhook";

    [Fact]
    public async Task Returns_disabled_when_tolerance_zero()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var verdict = await gate.EvaluateAsync(_flow, _trigger, headers, ReadOnlyMemory<byte>.Empty,
            toleranceSeconds: 0, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Disabled, verdict.Decision);
    }

    [Fact]
    public async Task Returns_timestamp_missing_when_no_header_present()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var verdict = await gate.EvaluateAsync(_flow, _trigger, headers, ReadOnlyMemory<byte>.Empty,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.TimestampMissing, verdict.Decision);
    }

    [Fact]
    public async Task Returns_skew_rejected_when_timestamp_outside_window()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000600));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000", // 600s back, tolerance 300
        };

        // Act
        var verdict = await gate.EvaluateAsync(_flow, _trigger, headers, ReadOnlyMemory<byte>.Empty,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.SkewRejected, verdict.Decision);
        Assert.Equal(600, verdict.Skew.TotalSeconds);
    }

    [Fact]
    public async Task First_delivery_returns_fresh()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000010));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000",
        };

        // Act
        var verdict = await gate.EvaluateAsync(_flow, _trigger, headers,
            new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("body-1")),
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, verdict.Decision);
    }

    [Fact]
    public async Task Same_body_and_timestamp_is_replay()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000010));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000",
        };
        var body = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("body-1"));

        // Act
        var first = await gate.EvaluateAsync(_flow, _trigger, headers, body,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);
        var second = await gate.EvaluateAsync(_flow, _trigger, headers, body,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, second.Decision);
    }

    [Fact]
    public async Task Explicit_nonce_header_overrides_body_hash()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000010));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);

        // Act
        var headersA = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000",
            ["X-Webhook-Delivery-Id"] = "delivery-1",
        };
        var headersB = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000",
            ["X-Webhook-Delivery-Id"] = "delivery-2",
        };
        var body = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("identical body"));
        var first = await gate.EvaluateAsync(_flow, _trigger, headersA, body,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: "X-Webhook-Delivery-Id");
        var second = await gate.EvaluateAsync(_flow, _trigger, headersB, body,
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: "X-Webhook-Delivery-Id");

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, second.Decision);
    }

    [Fact]
    public async Task Purge_removes_expired_entries()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000010));
        var store = new InMemoryWebhookReplayStore();
        var gate = new ReplayProtectionGate(store, clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000000",
        };
        var body = new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("body"));
        await gate.EvaluateAsync(_flow, _trigger, headers, body,
            toleranceSeconds: 60, timestampHeader: null, nonceHeader: null);

        // Act — advance time past expiry (ts + 2*tolerance = 1700000120)
        clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddSeconds(1700000200));
        var purged = await store.PurgeExpiredAsync(clock.GetUtcNow());

        // Assert
        Assert.Equal(1, purged);
    }

    [Fact]
    public async Task Future_timestamp_within_window_is_accepted()
    {
        // Arrange — clock skew where publisher is slightly ahead of server
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), clock);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Webhook-Timestamp"] = "1700000060", // 60s in the future
        };

        // Act
        var verdict = await gate.EvaluateAsync(_flow, _trigger, headers,
            new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("p")),
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: null);

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, verdict.Decision);
    }
}
