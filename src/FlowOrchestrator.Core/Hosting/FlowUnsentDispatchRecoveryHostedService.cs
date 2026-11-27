using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting.Internal;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Background service that re-delivers steps whose dispatch-ledger row was written but whose hand-off to
/// the runtime never returned — the process died mid-send, or the send hung.
/// </summary>
/// <remarks>
/// <para>
/// The engine records the ledger row first and annotates it with the runtime's job/message id once the
/// runtime accepts the step. A row still without an id after
/// <see cref="FlowRunControlOptions.UnsentDispatchTimeout"/> is leased through
/// <see cref="IFlowRunStore.LeaseUnsentDispatchesAsync"/> and the step is enqueued again. Startup recovery
/// (<see cref="FlowRunRecoveryHostedService"/>) cannot do this: it skips every ledgered key, and it runs
/// only when a host starts, possibly before the row is old enough to be judged.
/// </para>
/// <para>
/// Runs once when the host starts and then every half of the timeout, at most every 30 s. Every replica runs
/// it; the lease is atomic, so a row is re-delivered by one replica per timeout window. A store that does
/// not implement the lease (the interface default) makes every pass a no-op. Disabled when the timeout is
/// <see langword="null"/> or non-positive.
/// </para>
/// </remarks>
public sealed class FlowUnsentDispatchRecoveryHostedService : BackgroundService
{
    /// <summary>Longest interval between two passes, whatever the timeout.</summary>
    internal static readonly TimeSpan MaxSweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>Shortest interval between two passes, so a tiny timeout cannot spin the store.</summary>
    internal static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowRunControlOptions _options;
    private readonly ILogger<FlowUnsentDispatchRecoveryHostedService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Creates the scope each pass resolves the store and dispatcher from.</param>
    /// <param name="options">Supplies <see cref="FlowRunControlOptions.UnsentDispatchTimeout"/>.</param>
    /// <param name="logger">Logger.</param>
    public FlowUnsentDispatchRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        FlowRunControlOptions options,
        ILogger<FlowUnsentDispatchRecoveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>Returns the interval between two passes for <paramref name="timeout"/>: half of it, clamped to 1–30 s.</summary>
    /// <param name="timeout">The configured unsent-dispatch timeout; must be positive.</param>
    /// <returns>The sweep interval.</returns>
    internal static TimeSpan SweepIntervalFor(TimeSpan timeout)
    {
        var half = timeout / 2;
        return half < MinSweepInterval ? MinSweepInterval : half > MaxSweepInterval ? MaxSweepInterval : half;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.UnsentDispatchTimeout is not { } timeout || timeout <= TimeSpan.Zero)
        {
            return;
        }

        await SweepOnceAsync(timeout, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(SweepIntervalFor(timeout));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(timeout, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one pass in a fresh scope; any failure is logged and retried on the next tick.</summary>
    /// <param name="timeout">Minimum age of an unannotated ledger row.</param>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>The number of steps re-delivered, or 0 when the pass failed.</returns>
    internal async Task<int> SweepOnceAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var recoverer = new UnsentDispatchRecoverer(
                services.GetRequiredService<IFlowRunStore>(),
                services.GetRequiredService<FlowRegistry>(),
                services.GetRequiredService<IStepDispatcher>(),
                services.GetRequiredService<IOutputsRepository>(),
                _logger);
            return await recoverer.RecoverAsync(timeout, stoppingToken).ConfigureAwait(false);
        }
        // Only the stopping token's own cancellation ends the loop. Anything else — a store outage, an
        // OperationCanceledException a store raised for its own reasons — is a failed pass: logged, retried
        // on the next tick. Letting it escape would fault ExecuteAsync and, under
        // BackgroundServiceExceptionBehavior.StopHost, stop the host.
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            HostingLog.UnsentDispatchSweepFailed(_logger, ex);
            return 0;
        }
    }
}
