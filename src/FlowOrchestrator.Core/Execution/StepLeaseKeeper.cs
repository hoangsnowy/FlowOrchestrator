using System.Collections.Concurrent;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Holds the execution leases of the steps this process is running right now and renews them, so a live
/// handler is never taken over however long it runs.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process (a DI singleton). The engine registers a lease when it wins it and drops it when
/// the attempt is over (<see cref="Track"/>); <see cref="Hosting.FlowStepLeaseHostedService"/> calls
/// <see cref="RenewAsync"/> every <see cref="RenewInterval"/>. It renews exactly the leases of attempts that are
/// executing, never anything else: a lease whose owner died is no longer in any keeper, so it expires.
/// </para>
/// <para>
/// A lease missing from a renewal's answer was taken over by another worker (this process could not reach its
/// store for longer than the lease duration). Its handle reports <see cref="Handle.IsLost"/>, and the engine
/// discards the attempt's outcome instead of writing it; the store's fence rejects the write anyway.
/// </para>
/// <para>
/// Cost: one dictionary insert and one removal per step execution; one store round trip per renewal tick,
/// carrying every held lease, only while at least one is held.
/// </para>
/// </remarks>
public sealed class StepLeaseKeeper
{
    private readonly ConcurrentDictionary<StepLease, Handle> _held = new();

    /// <summary>Creates the keeper from the run-control options.</summary>
    /// <param name="options">Supplies <see cref="FlowRunControlOptions.StepLeaseDuration"/> and the renewal interval.</param>
    public StepLeaseKeeper(FlowRunControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Duration = options.StepLeaseDuration is { } duration && duration > TimeSpan.Zero ? duration : null;
        RenewInterval = RenewIntervalFor(Duration, options.StepLeaseRenewInterval);

        // Unique per process start, readable in a claim row: which host held the step.
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        OwnerId = id.Length <= 128 ? id : id[^128..];
    }

    /// <summary>Lease lifetime without renewal; <see langword="null"/> when leases are turned off.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Interval between two renewals: the configured value, capped at a third of <see cref="Duration"/>.</summary>
    public TimeSpan RenewInterval { get; }

    /// <summary>Identifies this process on the claims it takes.</summary>
    public string OwnerId { get; }

    /// <summary>Number of leases currently held; for diagnostics and tests.</summary>
    public int HeldCount => _held.Count;

    /// <summary>Returns the renewal interval for <paramref name="duration"/>: <paramref name="configured"/>, at most a third of the duration, at least 50 ms.</summary>
    /// <param name="duration">Lease duration; <see langword="null"/> when leases are off.</param>
    /// <param name="configured">The configured renewal interval.</param>
    /// <returns>The interval the keeper renews at.</returns>
    internal static TimeSpan RenewIntervalFor(TimeSpan? duration, TimeSpan configured)
    {
        var interval = configured > TimeSpan.Zero ? configured : FlowRunControlOptions.DefaultStepLeaseRenewInterval;
        if (duration is { } d && interval > d / 3)
        {
            interval = d / 3;
        }

        return interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval;
    }

    /// <summary>Starts renewing <paramref name="lease"/> until the returned handle is disposed.</summary>
    /// <param name="lease">A lease this process just acquired.</param>
    /// <returns>A handle that stops the renewal on dispose and reports whether the lease was lost.</returns>
    public Handle Track(StepLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var handle = new Handle(this, lease);
        _held[lease] = handle;
        return handle;
    }

    /// <summary>Renews every held lease in one store call and marks the ones the store no longer recognises as lost.</summary>
    /// <param name="store">The store holding the claims.</param>
    /// <returns>The number of leases renewed.</returns>
    /// <remarks>A store failure propagates and leaves every lease unchanged: they are retried on the next tick, and expire if the outage outlasts the lease.</remarks>
    public async Task<int> RenewAsync(IFlowRunRuntimeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Duration is not { } duration || _held.IsEmpty)
        {
            return 0;
        }

        var snapshot = _held.Keys.ToArray();
        var renewed = await store.RenewStepLeasesAsync(snapshot, duration).ConfigureAwait(false);
        var kept = new HashSet<StepLease>(renewed);
        // Renewal cadence, not a step-completion path: the Where reads clearer here.
        foreach (var lease in snapshot.Where(l => !kept.Contains(l)))
        {
            if (_held.TryGetValue(lease, out var handle))
            {
                handle.MarkLost();
            }
        }

        return kept.Count;
    }

    /// <summary>One tracked lease; dispose it when the attempt is over.</summary>
    public sealed class Handle : IDisposable
    {
        private readonly StepLeaseKeeper _keeper;
        private int _lost;

        internal Handle(StepLeaseKeeper keeper, StepLease lease)
        {
            _keeper = keeper;
            Lease = lease;
        }

        /// <summary>The tracked lease.</summary>
        public StepLease Lease { get; }

        /// <summary><see langword="true"/> once a renewal found that another worker took the lease over.</summary>
        public bool IsLost => Volatile.Read(ref _lost) == 1;

        internal void MarkLost() => Volatile.Write(ref _lost, 1);

        /// <summary>Stops renewing the lease. The claim itself is left as it is.</summary>
        public void Dispose() => _keeper._held.TryRemove(new KeyValuePair<StepLease, Handle>(Lease, this));
    }
}
