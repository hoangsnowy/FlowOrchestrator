using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Background service that periodically enforces run-level timeout deadlines, marking any active run
/// whose <c>TimeoutAtUtc</c> has passed as <c>TimedOut</c>. Without it a run whose step throws and
/// leaves nothing scheduled would sit <c>Running</c> indefinitely — the timeout would only ever be
/// applied lazily, if and when another step happened to be dispatched.
/// </summary>
/// <remarks>
/// Disabled when <see cref="FlowRunControlOptions.TimeoutEnforcementInterval"/> is <see langword="null"/>
/// or non-positive. The enforcer (<see cref="IRunTimeoutEnforcer"/>) is resolved from a fresh scope on
/// every tick because the engine depends on scoped services (e.g. the execution-context accessor).
/// </remarks>
internal sealed class FlowTimeoutEnforcementHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowRunControlOptions _options;
    private readonly ILogger<FlowTimeoutEnforcementHostedService> _logger;

    /// <summary>Initialises the timeout-enforcement service with its dependencies.</summary>
    public FlowTimeoutEnforcementHostedService(
        IServiceScopeFactory scopeFactory,
        FlowRunControlOptions options,
        ILogger<FlowTimeoutEnforcementHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.TimeoutEnforcementInterval;
        if (interval is null || interval.Value <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval.Value);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var enforcer = scope.ServiceProvider.GetRequiredService<IRunTimeoutEnforcer>();
            await enforcer.EnforceDueTimeoutsAsync(cancellationToken).ConfigureAwait(false);
        }
        // Only the stopping token's own cancellation ends the loop. Any other exception — including an
        // OperationCanceledException raised by a store for its own reasons (an HTTP client timeout, a
        // linked CTS) — is a failed sweep: logged, retried on the next tick. Letting a stray OCE escape
        // would fault ExecuteAsync and, under BackgroundServiceExceptionBehavior.StopHost, stop the host.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HostingLog.TimeoutSweepFailed(_logger, ex);
        }
    }
}
