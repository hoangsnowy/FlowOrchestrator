using System.Globalization;
using FlowOrchestrator.Dashboard.Webhooks.Replay;

namespace FlowOrchestrator.Dashboard.UnitTests.Replay;

/// <summary>
/// Releasing a replay key (compensation for a delivery that failed before its run started) is owner-bound:
/// only the registration that recorded an entry can remove it, so a release never opens a replay window.
/// </summary>
public sealed class ReplayKeyReleaseTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddMinutes(10);

    private static Dictionary<string, string> Headers(string deliveryId) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Webhook-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        ["X-Delivery-Id"] = deliveryId,
    };

    [Fact]
    public async Task Owner_can_forget_its_entry_and_the_nonce_is_registrable_again()
    {
        // Arrange
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await store.TryRegisterAsync(flowId, "webhook", "n-1", Expiry, owner);

        // Act
        var forgotten = await store.TryForgetAsync(flowId, "webhook", "n-1", owner);
        var again = await store.TryRegisterAsync(flowId, "webhook", "n-1", Expiry, Guid.NewGuid());

        // Assert
        Assert.True(forgotten);
        Assert.True(again);
    }

    [Fact]
    public async Task Another_owner_cannot_forget_the_entry()
    {
        // Arrange
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        await store.TryRegisterAsync(flowId, "webhook", "n-2", Expiry, Guid.NewGuid());

        // Act
        var forgotten = await store.TryForgetAsync(flowId, "webhook", "n-2", Guid.NewGuid());
        var replay = await store.TryRegisterAsync(flowId, "webhook", "n-2", Expiry, Guid.NewGuid());

        // Assert
        Assert.False(forgotten);
        Assert.False(replay);
    }

    [Fact]
    public async Task Losing_registration_does_not_take_over_the_entry()
    {
        // Arrange — the second register of the same nonce fails (replay); its token must not own the entry.
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();
        await store.TryRegisterAsync(flowId, "webhook", "n-3", Expiry, winner);
        await store.TryRegisterAsync(flowId, "webhook", "n-3", Expiry, loser);

        // Act
        var byLoser = await store.TryForgetAsync(flowId, "webhook", "n-3", loser);
        var byWinner = await store.TryForgetAsync(flowId, "webhook", "n-3", winner);

        // Assert
        Assert.False(byLoser);
        Assert.True(byWinner);
    }

    [Fact]
    public async Task Entry_recorded_without_an_owner_and_the_empty_token_are_never_forgotten()
    {
        // Arrange — the five-argument overload registers under an owner nobody holds.
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        await store.TryRegisterAsync(flowId, "webhook", "n-4", Expiry);
        await store.TryRegisterAsync(flowId, "webhook", "n-5", Expiry, Guid.Empty);

        // Act
        var anonymous = await store.TryForgetAsync(flowId, "webhook", "n-4", Guid.Empty);
        var emptyToken = await store.TryForgetAsync(flowId, "webhook", "n-5", Guid.Empty);

        // Assert
        Assert.False(anonymous);
        Assert.False(emptyToken);
    }

    [Fact]
    public async Task Forget_matches_a_long_nonce_through_its_storage_form()
    {
        // Arrange — over-long nonces are stored as a digest; release must find the same key.
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var nonce = new string('z', 1000);
        await store.TryRegisterAsync(flowId, "webhook", nonce, Expiry, owner);

        // Act
        var forgotten = await store.TryForgetAsync(flowId, "webhook", nonce, owner);

        // Assert
        Assert.True(forgotten);
    }

    [Fact]
    public async Task Gate_returns_a_registration_on_fresh_and_releasing_it_lets_the_retry_through()
    {
        // Arrange
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), TimeProvider.System);
        var flowId = Guid.NewGuid();
        var first = await gate.EvaluateAsync(flowId, "webhook", Headers("delivery-9"), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");

        // Act
        var released = await gate.ReleaseAsync(first.Registration!.Value, CancellationToken.None);
        var retry = await gate.EvaluateAsync(flowId, "webhook", Headers("delivery-9"), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");

        // Assert
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, first.Decision);
        Assert.True(released);
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, retry.Decision);
    }

    [Fact]
    public async Task Replay_verdict_carries_no_registration_and_a_forged_one_cannot_release()
    {
        // Arrange
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), TimeProvider.System);
        var flowId = Guid.NewGuid();
        var first = await gate.EvaluateAsync(flowId, "webhook", Headers("delivery-10"), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");
        var replay = await gate.EvaluateAsync(flowId, "webhook", Headers("delivery-10"), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");
        var forged = first.Registration!.Value with { RegistrationId = Guid.NewGuid() };

        // Act
        var released = await gate.ReleaseAsync(forged, CancellationToken.None);
        var again = await gate.EvaluateAsync(flowId, "webhook", Headers("delivery-10"), "{}"u8.ToArray(), 300, null, "X-Delivery-Id");

        // Assert
        Assert.Null(replay.Registration);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, replay.Decision);
        Assert.False(released);
        Assert.Equal(ReplayProtectionGate.Decision.ReplayRejected, again.Decision);
    }

    [Fact]
    public async Task Body_only_signed_registration_is_releasable()
    {
        // Arrange — signature verified, no signed timestamp: the body-hash path also returns its registration.
        var gate = new ReplayProtectionGate(new InMemoryWebhookReplayStore(), TimeProvider.System);
        var flowId = Guid.NewGuid();
        var body = "{\"zen\":1}"u8.ToArray();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var first = await gate.EvaluateAsync(flowId, "webhook", headers, body, 300, null, null,
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);

        // Act
        var released = await gate.ReleaseAsync(first.Registration!.Value, CancellationToken.None);
        var redelivery = await gate.EvaluateAsync(flowId, "webhook", headers, body, 300, null, null,
            signatureVerified: true, authenticatedTimestamp: null, authenticatedNonce: null);

        // Assert
        Assert.True(released);
        Assert.Equal(ReplayProtectionGate.Decision.Fresh, redelivery.Decision);
    }

    [Fact]
    public async Task Purge_does_not_drop_an_entry_re_registered_after_a_release()
    {
        // Arrange — compare-and-remove: purge only removes the exact (expired) entry it observed.
        var store = new InMemoryWebhookReplayStore();
        var flowId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        await store.TryRegisterAsync(flowId, "webhook", "n-6", DateTimeOffset.UtcNow.AddSeconds(-5), owner);
        await store.TryForgetAsync(flowId, "webhook", "n-6", owner);
        await store.TryRegisterAsync(flowId, "webhook", "n-6", Expiry, Guid.NewGuid());

        // Act
        var purged = await store.PurgeExpiredAsync(DateTimeOffset.UtcNow);
        var replay = await store.TryRegisterAsync(flowId, "webhook", "n-6", Expiry, Guid.NewGuid());

        // Assert
        Assert.Equal(0, purged);
        Assert.False(replay);
    }
}
