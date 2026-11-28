using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Servers;

/// <summary>Payload of <c>GET {base}/api/servers</c>: the recorded servers with a computed status.</summary>
/// <param name="Tracked"><see langword="false"/> when no <see cref="IFlowServerStore"/> is registered: the storage backend does not track servers.</param>
/// <param name="HeartbeatIntervalSeconds">The configured heartbeat interval.</param>
/// <param name="StaleAfterSeconds">Age of the latest heartbeat beyond which a server is <see cref="ServersView.Stale"/>.</param>
/// <param name="ServerTime">The instant the statuses were computed against.</param>
/// <param name="Servers">Active servers first, then by machine name, process id and server id.</param>
internal sealed record ServersResponse(
    bool Tracked,
    double HeartbeatIntervalSeconds,
    double StaleAfterSeconds,
    DateTimeOffset ServerTime,
    IReadOnlyList<ServerView> Servers);

/// <summary>One server row of <see cref="ServersResponse"/>.</summary>
/// <param name="ServerId">The full server id (step-lease owner id).</param>
/// <param name="MachineName">Host name.</param>
/// <param name="ProcessId">Process id.</param>
/// <param name="Runtime">Runtime adapter name.</param>
/// <param name="Version">FlowOrchestrator.Core informational version, or <see langword="null"/>.</param>
/// <param name="StartedAt">When the store first recorded the server.</param>
/// <param name="LastHeartbeatAt">When the store received the latest heartbeat.</param>
/// <param name="LastSeenSeconds">Whole seconds since <paramref name="LastHeartbeatAt"/>, never negative.</param>
/// <param name="ActiveStepCount">Steps executing on the server at its latest heartbeat.</param>
/// <param name="FlowCount">Flows the server registered.</param>
/// <param name="Status"><see cref="ServersView.Active"/> or <see cref="ServersView.Stale"/>.</param>
internal sealed record ServerView(
    string ServerId,
    string MachineName,
    int ProcessId,
    string Runtime,
    string? Version,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    long LastSeenSeconds,
    int ActiveStepCount,
    int FlowCount,
    string Status);

/// <summary>Builds the Servers view payload; O(s log s) for s recorded servers, once per poll, off the engine path.</summary>
internal static class ServersView
{
    /// <summary>Status of a server whose latest heartbeat is within the stale threshold.</summary>
    public const string Active = "Active";

    /// <summary>Status of a server that missed <see cref="FlowServerOptions.MissedHeartbeatsBeforeStale"/> heartbeats in a row.</summary>
    public const string Stale = "Stale";

    /// <summary>Returns the status of a server last heard from at <paramref name="lastHeartbeatAt"/>.</summary>
    /// <param name="lastHeartbeatAt">The server's latest heartbeat.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="staleAfter">The stale threshold.</param>
    /// <returns><see cref="Stale"/> once the heartbeat is older than <paramref name="staleAfter"/>; otherwise <see cref="Active"/>, a heartbeat dated in the future (clock skew) included.</returns>
    public static string StatusFor(DateTimeOffset lastHeartbeatAt, DateTimeOffset now, TimeSpan staleAfter)
        => now - lastHeartbeatAt > staleAfter ? Stale : Active;

    /// <summary>The payload for a storage backend that does not track servers.</summary>
    /// <param name="options">The server options.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>An untracked, empty response.</returns>
    public static ServersResponse NotTracked(FlowServerOptions options, DateTimeOffset now)
        => new(false, options.HeartbeatInterval.TotalSeconds, options.StaleAfter.TotalSeconds, now, []);

    /// <summary>Computes each server's status against <paramref name="now"/> and orders the rows.</summary>
    /// <param name="servers">The rows from <see cref="IFlowServerStore.GetServersAsync"/>.</param>
    /// <param name="now">This host's current instant; used only for rows whose store reports no <see cref="FlowServerRecord.ObservedAt"/>.</param>
    /// <param name="options">Supplies <see cref="FlowServerOptions.StaleAfter"/>.</param>
    /// <returns>The tracked response.</returns>
    public static ServersResponse Build(IReadOnlyList<FlowServerRecord> servers, DateTimeOffset now, FlowServerOptions options)
    {
        var staleAfter = options.StaleAfter;
        var rows = new ServerView[servers.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var s = servers[i];
            // Age by the clock that stamped the heartbeat (the store's), not this host's: see
            // FlowServerRecord.ObservedAt.
            var observedAt = s.ObservedAt ?? now;
            var lastSeen = observedAt - s.LastHeartbeatAt;
            rows[i] = new ServerView(
                s.ServerId,
                s.MachineName,
                s.ProcessId,
                s.Runtime,
                s.Version,
                s.StartedAt,
                s.LastHeartbeatAt,
                lastSeen > TimeSpan.Zero ? (long)lastSeen.TotalSeconds : 0,
                s.ActiveStepCount,
                s.FlowCount,
                StatusFor(s.LastHeartbeatAt, observedAt, staleAfter));
        }

        Array.Sort(rows, static (a, b) =>
        {
            var byStatus = string.CompareOrdinal(a.Status, b.Status); // "Active" < "Stale"
            if (byStatus != 0) return byStatus;
            var byMachine = string.Compare(a.MachineName, b.MachineName, StringComparison.OrdinalIgnoreCase);
            if (byMachine != 0) return byMachine;
            var byPid = a.ProcessId.CompareTo(b.ProcessId);
            return byPid != 0 ? byPid : string.CompareOrdinal(a.ServerId, b.ServerId);
        });

        return new ServersResponse(true, options.HeartbeatInterval.TotalSeconds, staleAfter.TotalSeconds, now, rows);
    }
}
