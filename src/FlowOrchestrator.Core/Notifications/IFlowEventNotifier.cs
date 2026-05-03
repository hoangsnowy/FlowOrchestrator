namespace FlowOrchestrator.Core.Notifications;

/// <summary>
/// Publishes flow lifecycle events for realtime consumers (dashboard SSE, log sinks, custom listeners).
/// Implementations MUST be non-blocking: the engine awaits this on its hot path, so any backing
/// transport should buffer (e.g. <see cref="System.Threading.Channels.Channel"/>) and return synchronously.
/// </summary>
/// <remarks>
/// The default registration is <see cref="NoopFlowEventNotifier"/>; replacing it (e.g. with the
/// dashboard's <c>SseFlowEventBroadcaster</c>) is what activates realtime push.
/// Engine-side calls are wrapped in try/catch — a thrown notifier never aborts a run.
/// </remarks>
public interface IFlowEventNotifier
{
    /// <summary>
    /// Publishes <paramref name="evt"/> to any subscribers. Implementations should not block;
    /// dropped events are preferable to a stalled engine.
    /// </summary>
    /// <param name="evt">The lifecycle event to publish.</param>
    /// <param name="ct">Token cancelled when the engine shuts down.</param>
    ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default);
}
