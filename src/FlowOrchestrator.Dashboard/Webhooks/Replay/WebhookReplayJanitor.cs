using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.Webhooks.Replay;

/// <summary>
/// Hosted service that periodically purges expired replay-nonce entries from
/// the active <see cref="IWebhookReplayStore"/>. Runs once per minute by default.
/// </summary>
public sealed class WebhookReplayJanitor : BackgroundService
{
    private readonly IWebhookReplayStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly ILogger<WebhookReplayJanitor>? _logger;

    /// <summary>Creates the janitor with default 60 s interval.</summary>
    /// <param name="store">Replay store to purge.</param>
    /// <param name="clock">Time provider used for "now".</param>
    /// <param name="logger">Optional structured logger.</param>
    public WebhookReplayJanitor(IWebhookReplayStore store, TimeProvider clock, ILogger<WebhookReplayJanitor>? logger = null)
        : this(store, clock, TimeSpan.FromMinutes(1), logger)
    {
    }

    /// <summary>Creates the janitor with a custom purge interval (used by tests).</summary>
    /// <param name="store">Replay store to purge.</param>
    /// <param name="clock">Time provider used for "now".</param>
    /// <param name="interval">Purge cadence.</param>
    /// <param name="logger">Optional structured logger.</param>
    public WebhookReplayJanitor(IWebhookReplayStore store, TimeProvider clock, TimeSpan interval, ILogger<WebhookReplayJanitor>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var purged = await _store.PurgeExpiredAsync(_clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                    if (purged > 0 && _logger is not null)
                        _logger.LogDebug("WebhookReplayJanitor purged {Count} expired nonces.", purged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "WebhookReplayJanitor purge cycle failed; will retry on next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
