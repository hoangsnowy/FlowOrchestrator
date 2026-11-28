namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// One FlowOrchestrator process (a "server", i.e. a replica) as recorded by its heartbeat in
/// <see cref="IFlowServerStore"/>.
/// </summary>
/// <remarks>
/// <see cref="ServerId"/> is the owner id the process writes on the step leases it takes
/// (<c>FlowStepLeases.Owner</c>) and on the attempts it runs (<see cref="FlowStepAttemptRecord.ServerId"/>),
/// so a lease or an attempt row maps to the server that holds or ran it.
/// </remarks>
public sealed record FlowServerRecord
{
    /// <summary>
    /// Identifies the process for its whole lifetime: <c>{MachineName}:{ProcessId}:{guid}</c>, unique per process
    /// start, at most 128 characters.
    /// </summary>
    public string ServerId { get; init; } = string.Empty;

    /// <summary>Host name of the machine (or container) the process runs on.</summary>
    public string MachineName { get; init; } = string.Empty;

    /// <summary>Operating-system process id; unique only together with <see cref="MachineName"/> and a start time.</summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// Runtime adapter executing the process's steps: <c>Hangfire</c>, <c>InMemory</c>, <c>ServiceBus</c>, or the
    /// name a custom runtime reports.
    /// </summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>Informational version of the <c>FlowOrchestrator.Core</c> assembly the process loaded, or <see langword="null"/> when unknown.</summary>
    public string? Version { get; init; }

    /// <summary>When the store first recorded the process, by the store's clock.</summary>
    /// <remarks>Ignored on <see cref="IFlowServerStore.UpsertServerAsync"/>: the store stamps it on insert and keeps it on update.</remarks>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the store received the process's latest heartbeat, by the store's clock.</summary>
    /// <remarks>Ignored on <see cref="IFlowServerStore.UpsertServerAsync"/>: the store stamps it on every write.</remarks>
    public DateTimeOffset LastHeartbeatAt { get; init; }

    /// <summary>Step leases the process held at its latest heartbeat — the steps executing on it at that moment.</summary>
    /// <remarks>Always 0 when step leases are turned off (<c>FlowRunControlOptions.StepLeaseDuration</c> is <see langword="null"/>).</remarks>
    public int ActiveStepCount { get; init; }

    /// <summary>Flows the process registered with <c>AddFlow&lt;T&gt;()</c>.</summary>
    public int FlowCount { get; init; }

    /// <summary>
    /// The store's clock at the moment <see cref="IFlowServerStore.GetServersAsync"/> read the row, or
    /// <see langword="null"/> when the store does not report it.
    /// </summary>
    /// <remarks>
    /// <see cref="LastHeartbeatAt"/> is stamped by the store's clock, so a heartbeat's age must be measured
    /// against the same clock: comparing it with the reading host's clock turns any skew between that host
    /// and the database straight into a wrong Active / Stale status. Readers fall back to their own clock when
    /// this is <see langword="null"/>. Ignored on <see cref="IFlowServerStore.UpsertServerAsync"/>.
    /// </remarks>
    public DateTimeOffset? ObservedAt { get; init; }
}
