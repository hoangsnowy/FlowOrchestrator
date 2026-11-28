using System.Reflection;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Hosting;

/// <summary>
/// Keeps this process's row in <see cref="IFlowServerStore"/> alive: registers it on start, heartbeats every
/// <see cref="FlowServerOptions.HeartbeatInterval"/>, purges rows silent for <see cref="FlowServerOptions.PurgeAfter"/>
/// now and then, and removes its own row on graceful shutdown.
/// </summary>
/// <remarks>
/// <para>
/// The row's id is <see cref="StepLeaseKeeper.OwnerId"/>, the owner the process writes on its step leases and step
/// attempts, so a lease or an attempt maps to the server that took it. The active-step count is
/// <see cref="StepLeaseKeeper.HeldCount"/> at the moment of the heartbeat.
/// </para>
/// <para>
/// Off the step hot path: one store write per interval, one purge statement every few minutes. A failed write is
/// logged and retried on the next tick; the dashboard shows the server as stale meanwhile. Without a registered
/// <see cref="IFlowServerStore"/> (a custom storage backend) or with <see cref="FlowServerOptions.Enabled"/> off,
/// the service returns at once.
/// </para>
/// </remarks>
internal sealed class FlowServerHeartbeatHostedService : BackgroundService
{
    /// <summary>Upper bound on the shutdown removal, whatever token the host passes to <see cref="StopAsync"/>.</summary>
    internal static readonly TimeSpan RemoveOnStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longest interval between two purges, whatever <see cref="FlowServerOptions.PurgeAfter"/> is.</summary>
    private static readonly TimeSpan MaxPurgeInterval = TimeSpan.FromMinutes(10);

    private const int MaxMachineNameLength = 256;
    private const int MaxRuntimeLength = 64;
    private const int MaxVersionLength = 128;

    private static readonly string? CoreVersion = Truncate(
        typeof(FlowServerRecord).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        MaxVersionLength);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StepLeaseKeeper _keeper;
    private readonly FlowServerOptions _options;
    private readonly FlowRegistry _flows;
    private readonly ILogger<FlowServerHeartbeatHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _beatsPerPurge;
    private string? _runtime;
    private volatile bool _storeFound;
    private bool _registeredLogged;

    /// <summary>Initialises the service.</summary>
    /// <param name="scopeFactory">Creates the scope each store call resolves <see cref="IFlowServerStore"/> from.</param>
    /// <param name="keeper">Supplies the server id and the number of leases held.</param>
    /// <param name="options">Heartbeat interval and purge age.</param>
    /// <param name="flows">The registered flows, counted on each heartbeat.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock of the heartbeat timer; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    public FlowServerHeartbeatHostedService(
        IServiceScopeFactory scopeFactory,
        StepLeaseKeeper keeper,
        FlowServerOptions options,
        FlowRegistry flows,
        ILogger<FlowServerHeartbeatHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _keeper = keeper;
        _options = options;
        _flows = flows;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beatsPerPurge = BeatsPerPurge(options.HeartbeatInterval, options.PurgeAfter);
    }

    /// <summary>Outcome of one heartbeat attempt.</summary>
    internal enum HeartbeatOutcome
    {
        /// <summary>The heartbeat is durable.</summary>
        Written,

        /// <summary>The store rejected or failed the write; retried on the next tick.</summary>
        Failed,

        /// <summary>No <see cref="IFlowServerStore"/> is registered; the service stops.</summary>
        NoStore,
    }

    /// <summary>Heartbeats between two purges: a quarter of the purge age, clamped to one interval .. 10 minutes.</summary>
    /// <param name="interval">The heartbeat interval.</param>
    /// <param name="purgeAfter">The purge age.</param>
    /// <returns>At least 1.</returns>
    internal static int BeatsPerPurge(TimeSpan interval, TimeSpan purgeAfter)
    {
        var purgeInterval = purgeAfter / 4;
        if (purgeInterval > MaxPurgeInterval)
        {
            purgeInterval = MaxPurgeInterval;
        }

        return Math.Max(1, (int)(purgeInterval.Ticks / interval.Ticks));
    }

    /// <summary>Derives a runtime name from a dispatcher type: its name without the <c>StepDispatcher</c> suffix.</summary>
    /// <param name="dispatcherType">The registered dispatcher's concrete type, or <see langword="null"/>.</param>
    /// <returns><c>HangfireStepDispatcher</c> → <c>Hangfire</c>; <c>Unknown</c> without a type.</returns>
    internal static string RuntimeNameFor(Type? dispatcherType)
    {
        if (dispatcherType is null)
        {
            return "Unknown";
        }

        const string Suffix = "StepDispatcher";
        var name = dispatcherType.Name;
        return name.Length > Suffix.Length && name.EndsWith(Suffix, StringComparison.Ordinal)
            ? name[..^Suffix.Length]
            : name;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        // Created before the first beat, so a tick is never lost between the beat and the timer.
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _timeProvider);
        try
        {
            if (await HeartbeatOnceAsync(stoppingToken).ConfigureAwait(false) == HeartbeatOutcome.NoStore)
            {
                ServerRegistryLog.ServerStoreMissing(_logger);
                return;
            }

            await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);

            var beats = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await HeartbeatOnceAsync(stoppingToken).ConfigureAwait(false);
                if (++beats % _beatsPerPurge == 0)
                {
                    await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; StopAsync removes the row.
        }
    }

    /// <summary>Stops the heartbeat loop, then removes this server's row, best effort.</summary>
    /// <param name="cancellationToken">The host's shutdown budget; the removal is also bounded by <see cref="RemoveOnStopTimeout"/>.</param>
    /// <returns>A task that never faults.</returns>
    /// <remarks>
    /// Any failure is logged and swallowed: the row then turns stale and is purged by another replica. The loop is
    /// stopped first so a heartbeat in flight cannot re-insert the row after it was removed.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown budget exhausted while the loop was finishing; still try the removal below.
        }

        if (_storeFound)
        {
            await RemoveSelfAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one heartbeat in a fresh scope.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>What happened; a failure is logged, never thrown.</returns>
    internal async Task<HeartbeatOutcome> HeartbeatOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var store = services.GetService<IFlowServerStore>();
            if (store is null)
            {
                return HeartbeatOutcome.NoStore;
            }

            _storeFound = true;
            _runtime ??= string.IsNullOrWhiteSpace(_options.RuntimeName)
                ? RuntimeNameFor(services.GetService<IStepDispatcher>()?.GetType())
                : Truncate(_options.RuntimeName.Trim(), MaxRuntimeLength)!;

            await store.UpsertServerAsync(CreateHeartbeat(_runtime), cancellationToken).ConfigureAwait(false);
            if (!_registeredLogged)
            {
                _registeredLogged = true;
                ServerRegistryLog.ServerRegistered(_logger, _keeper.OwnerId, _runtime, _options.HeartbeatInterval.TotalSeconds);
            }

            return HeartbeatOutcome.Written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ServerRegistryLog.HeartbeatFailed(_logger, ex, _keeper.OwnerId);
            return HeartbeatOutcome.Failed;
        }
    }

    /// <summary>Deletes the rows of servers silent for <see cref="FlowServerOptions.PurgeAfter"/>.</summary>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>The number of rows removed; 0 when the pass failed.</returns>
    internal async Task<int> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IFlowServerStore>() is not { } store)
            {
                return 0;
            }

            var removed = await store.PurgeServersAsync(_options.PurgeAfter, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                ServerRegistryLog.ServersPurged(_logger, removed, _options.PurgeAfter.TotalSeconds);
            }

            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ServerRegistryLog.PurgeFailed(_logger, ex, _options.PurgeAfter.TotalSeconds);
            return 0;
        }
    }

    /// <summary>Builds this process's heartbeat; O(1).</summary>
    /// <param name="runtime">The resolved runtime name.</param>
    /// <returns>The record to upsert; its timestamps are stamped by the store.</returns>
    internal FlowServerRecord CreateHeartbeat(string runtime) => new()
    {
        ServerId = _keeper.OwnerId,
        MachineName = Truncate(Environment.MachineName, MaxMachineNameLength) ?? string.Empty,
        ProcessId = Environment.ProcessId,
        Runtime = runtime,
        Version = CoreVersion,
        ActiveStepCount = _keeper.HeldCount,
        FlowCount = _flows.Flows.Count,
    };

    private async Task RemoveSelfAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RemoveOnStopTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IFlowServerStore>() is { } store)
            {
                await store.RemoveServerAsync(_keeper.OwnerId, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never out of StopAsync, cancellation included: a stale row is purged by another replica.
            ServerRegistryLog.RemoveOnShutdownFailed(_logger, ex, _keeper.OwnerId);
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
