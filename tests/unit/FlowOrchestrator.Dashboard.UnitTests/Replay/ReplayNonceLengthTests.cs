using System.Globalization;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks.Replay;

namespace FlowOrchestrator.Dashboard.UnitTests.Replay;

/// <summary>
/// A replay nonce taken from an unsigned header is caller-controlled and uncapped. The gate must hand the store
/// a key that fits the 256-character SQL / PostgreSQL column, and a long nonce must still be detected as a
/// replay the second time it is seen.
/// </summary>
public sealed class ReplayNonceLengthTests
{
    private static Dictionary<string, string> Headers(string deliveryId) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Webhook-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ["X-Delivery-Id"] = deliveryId,
    };

    [Fact]
    public async Task Gate_never_hands_the_store_a_nonce_longer_than_the_sql_column()
    {
        // Arrange
        var store = new RecordingReplayStore();
        var gate = new ReplayProtectionGate(store, TimeProvider.System);

        // Act
        await gate.EvaluateAsync(Guid.NewGuid(), "webhook", Headers(new string('d', 5000)), "{}"u8.ToArray(),
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: "X-Delivery-Id");

        // Assert
        Assert.NotNull(store.LastNonce);
        Assert.True(store.LastNonce!.Length <= WebhookReplayNonceStorage.MaxStoredLength, $"nonce length {store.LastNonce.Length}");
    }

    [Fact]
    public async Task Short_nonce_reaches_the_store_unchanged()
    {
        // Arrange
        var store = new RecordingReplayStore();
        var gate = new ReplayProtectionGate(store, TimeProvider.System);

        // Act
        await gate.EvaluateAsync(Guid.NewGuid(), "webhook", Headers("delivery-0042"), "{}"u8.ToArray(),
            toleranceSeconds: 300, timestampHeader: null, nonceHeader: "X-Delivery-Id");

        // Assert
        Assert.Equal("delivery-0042", store.LastNonce);
    }

    [Fact]
    public async Task Long_nonce_seen_twice_is_rejected_as_a_replay()
    {
        // Arrange
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), TimeProvider.System);
        var flowId = Guid.NewGuid();
        var deliveryId = new string('r', 1000);

        // Act
        var first = await gate.EvaluateAsync(flowId, "webhook", Headers(deliveryId), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");
        var second = await gate.EvaluateAsync(flowId, "webhook", Headers(deliveryId), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, second.Decision);
    }

    [Fact]
    public async Task In_memory_store_bounds_a_long_nonce_like_the_sql_stores()
    {
        // Arrange — a direct caller (not the gate) gets the same digest key the SQL stores would hold.
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        var nonce = new string('m', 300);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        var first = await store.TryRegisterAsync(flowId, "webhook", nonce, expires);
        var digestForm = await store.TryRegisterAsync(flowId, "webhook", WebhookReplayNonceStorage.ToStorageForm(nonce), expires);

        // Assert
        Assert.True(first);
        Assert.False(digestForm);
    }

    private sealed class RecordingReplayStore : IWebhookReplayStore
    {
        public string? LastNonce { get; private set; }

        public ValueTask<bool> TryRegisterAsync(Guid flowId, string triggerKey, string nonce, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            LastNonce = nonce;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> TryRegisterAsync(Guid flowId, string triggerKey, string nonce, DateTimeOffset expiresAt, Guid registrationId, CancellationToken ct = default)
            => TryRegisterAsync(flowId, triggerKey, nonce, expiresAt, ct);

        public ValueTask<bool> TryForgetAsync(Guid flowId, string triggerKey, string nonce, Guid registrationId, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct = default) => ValueTask.FromResult(0);
    }
}
