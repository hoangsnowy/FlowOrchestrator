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
/// Background service that keeps this process's step leases alive and redelivers steps whose lease expired.
/// </summary>
/// <remarks>
/// <para>
/// Two loops. The renewal loop calls <see cref="StepLeaseKeeper.RenewAsync"/> every
/// <see cref="StepLeaseKeeper.RenewInterval"/>, so a step that is executing here is never taken over. The sweep
/// loop runs once at startup and then every half of <see cref="FlowRunControlOptions.StepLeaseDuration"/> (1–30 s):
/// it selects steps whose lease expired without a terminal status — their owner died mid-handler, between its
/// claim and its start write, or between its handler and its completion write — and hands them to the runtime
/// again (<see cref="IFlowRunRuntimeStore.LeaseExpiredStepLeasesAsync"/>). The runtime's own redelivery covers
/// most of these, but it can come while the lease is still live (a Hangfire invisibility timeout or Service Bus
/// lock shorter than the lease) and then never again; this sweep is what guarantees the step runs.
/// </para>
/// <para>
/// Every replica runs it; the selection is atomic, so a step is redelivered by one replica per lease window.
/// Disabled when leases are turned off (<see cref="FlowRunControlOptions.StepLeaseDuration"/> is
/// <see langword="null"/> or non-positive). A failed pass is logged and retried on the next tick.
/// </para>
/// </remarks>
public sealed class FlowStepLeaseHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StepLeaseKeeper _keeper;
    private readonly ILogger<FlowStepLeaseHostedService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Creates the scope each pass resolves the store and dispatcher from.</param>
    /// <param name="keeper">The process's lease keeper.</param>
    /// <param name="logger">Logger.</param>
    public FlowStepLeaseHostedService(IServiceScopeFactory scopeFactory, StepLeaseKeeper keeper, ILogger<FlowStepLeaseHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _keeper = keeper;
        _logger = logger;
    }

    /// <summary>Returns the sweep interval for <paramref name="duration"/>: half of it, clamped to 1–30 s.</summary>
    /// <param name="duration">The lease duration; must be positive.</param>
    /// <returns>The sweep interval.</returns>
    internal static TimeSpan SweepIntervalFor(TimeSpan duration) => FlowUnsentDispatchRecoveryHostedService.SweepIntervalFor(duration);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_keeper.Duration is not { } duration)
        {
            return;
        }

        try
        {
            await Task.WhenAll(RenewLoopAsync(stoppingToken), SweepLoopAsync(duration, stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RenewLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_keeper.RenewInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RenewOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepLoopAsync(TimeSpan duration, CancellationToken stoppingToken)
    {
        await SweepOnceAsync(duration, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(SweepIntervalFor(duration));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(duration, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Renews every held lease once; any failure is logged and retried on the next tick.</summary>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>The number of leases renewed, or 0 when the pass failed.</returns>
    internal async Task<int> RenewOnceAsync(CancellationToken stoppingToken)
    {
        if (_keeper.HeldCount == 0)
        {
            return 0;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await _keeper.RenewAsync(scope.ServiceProvider.GetRequiredService<IFlowRunRuntimeStore>()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            HostingLog.StepLeaseRenewalFailed(_logger, ex, _keeper.HeldCount);
            return 0;
        }
    }

    /// <summary>Runs one sweep pass in a fresh scope; any failure is logged and retried on the next tick.</summary>
    /// <param name="duration">The lease duration, used as the redelivery backoff.</param>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>The number of steps redelivered, or 0 when the pass failed.</returns>
    internal async Task<int> SweepOnceAsync(TimeSpan duration, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var recoverer = new ExpiredStepLeaseRecoverer(
                services.GetRequiredService<IFlowRunRuntimeStore>(),
                services.GetRequiredService<FlowRegistry>(),
                services.GetRequiredService<IStepDispatcher>(),
                services.GetRequiredService<IOutputsRepository>(),
                _logger);

            // Backoff = one lease duration: a redelivered attempt that takes the lease over and dies again is
            // judged again only once its own lease could have expired.
            return await recoverer.RecoverAsync(duration, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            HostingLog.ExpiredLeaseSweepFailed(_logger, ex);
            return 0;
        }
    }
}
