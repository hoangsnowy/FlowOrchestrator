using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.Dashboard.UnitTests.Replay;

/// <summary>
/// Tests <see cref="WebhookReplayJanitor"/> as a hosted-service. The
/// purge-on-tick path is exercised by directly invoking
/// <see cref="IWebhookReplayStore.PurgeExpiredAsync"/> in
/// <c>ReplayProtectionGateTests</c>; here we verify the BackgroundService
/// lifecycle (start, stop, dispose) is safe and the constructor validates
/// its inputs. Verifying tick-driven purges is brittle on CI because
/// PeriodicTimer + a FakeTimeProvider ticks asynchronously and there is
/// no public sync point — running tests would have to spin-wait.
/// </summary>
public sealed class WebhookReplayJanitorTests
{
    [Fact]
    public async Task StartAsync_then_StopAsync_completes_cleanly()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        var store = new InMemoryWebhookReplayStore();
        using var janitor = new WebhookReplayJanitor(store, clock, TimeSpan.FromSeconds(60), logger: null);
        using var cts = new CancellationTokenSource();

        // Act + Assert — should not throw on a clean start/stop cycle.
        await janitor.StartAsync(cts.Token);
        await janitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_during_run_unwinds_without_throwing()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1700000000));
        using var janitor = new WebhookReplayJanitor(new InMemoryWebhookReplayStore(), clock, TimeSpan.FromSeconds(60), logger: null);
        using var cts = new CancellationTokenSource();

        // Act
        await janitor.StartAsync(cts.Token);
        await cts.CancelAsync();
        await janitor.StopAsync(CancellationToken.None);

        // Assert — no exception escapes (cancellation is normal shutdown for the loop).
    }

    [Fact]
    public void Constructor_rejects_non_positive_interval()
    {
        // Arrange + Act + Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WebhookReplayJanitor(new InMemoryWebhookReplayStore(), TimeProvider.System, TimeSpan.Zero, logger: null));
    }

    [Fact]
    public void Constructor_rejects_null_store()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WebhookReplayJanitor(store: null!, TimeProvider.System, TimeSpan.FromMinutes(1), logger: null));
    }

    [Fact]
    public void Constructor_rejects_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WebhookReplayJanitor(new InMemoryWebhookReplayStore(), clock: null!, TimeSpan.FromMinutes(1), logger: null));
    }
}
