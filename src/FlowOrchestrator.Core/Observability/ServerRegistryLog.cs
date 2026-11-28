using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for the server registry
/// (<see cref="Hosting.FlowServerHeartbeatHostedService"/>). Event IDs 5060-5069 of the hosting block.
/// </summary>
internal static partial class ServerRegistryLog
{
    [LoggerMessage(EventId = 5060, Level = LogLevel.Information, Message = "Registered server {ServerId} ({Runtime}); heartbeat every {IntervalSeconds} s.")]
    public static partial void ServerRegistered(ILogger logger, string serverId, string runtime, double intervalSeconds);

    [LoggerMessage(EventId = 5061, Level = LogLevel.Debug, Message = "No IFlowServerStore is registered: the storage backend does not track servers, so this process does not appear in the dashboard's Servers view.")]
    public static partial void ServerStoreMissing(ILogger logger);

    [LoggerMessage(EventId = 5062, Level = LogLevel.Warning, Message = "Server heartbeat for {ServerId} failed; will retry on the next tick. The dashboard shows this server as stale if heartbeats keep failing.")]
    public static partial void HeartbeatFailed(ILogger logger, Exception ex, string serverId);

    [LoggerMessage(EventId = 5063, Level = LogLevel.Warning, Message = "Purging servers not seen for {PurgeAfterSeconds} s failed; will retry on the next purge tick.")]
    public static partial void PurgeFailed(ILogger logger, Exception ex, double purgeAfterSeconds);

    [LoggerMessage(EventId = 5064, Level = LogLevel.Information, Message = "Purged {Count} server(s) not seen for {PurgeAfterSeconds} s.")]
    public static partial void ServersPurged(ILogger logger, int count, double purgeAfterSeconds);

    [LoggerMessage(EventId = 5065, Level = LogLevel.Warning, Message = "Removing server {ServerId} on shutdown failed; its row turns stale and is purged later.")]
    public static partial void RemoveOnShutdownFailed(ILogger logger, Exception ex, string serverId);
}
