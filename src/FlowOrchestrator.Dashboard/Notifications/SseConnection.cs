using System.Threading.Channels;
using FlowOrchestrator.Core.Notifications;

namespace FlowOrchestrator.Dashboard.Notifications;

/// <summary>
/// Per-client buffered queue used by <see cref="SseFlowEventBroadcaster"/> to fan out
/// <see cref="FlowLifecycleEvent"/> values to a single SSE response stream.
/// </summary>
/// <remarks>
/// The channel is bounded with <see cref="BoundedChannelFullMode.DropOldest"/>: a slow reader
/// (e.g. a browser on a poor network) never blocks the publisher, and the broadcaster's memory
/// footprint is capped at <c>capacity × subscribers</c>. The trade-off is that bursts beyond
/// the capacity drop the oldest events first; the dashboard tolerates this because every event
/// has an idempotent refetch path on the client.
/// </remarks>
public sealed class SseConnection
{
    /// <summary>Default per-connection channel capacity.</summary>
    public const int DefaultCapacity = 256;

    private readonly Channel<FlowLifecycleEvent> _channel;

    /// <summary>Initialises a new connection registered against a request abort token.</summary>
    /// <param name="requestAborted">Cancelled when the underlying HTTP request ends.</param>
    /// <param name="runIdFilter">When non-<see langword="null"/>, only events with a matching <see cref="FlowLifecycleEvent.RunId"/> are delivered.</param>
    /// <param name="capacity">Maximum buffered events; oldest are dropped when full.</param>
    public SseConnection(CancellationToken requestAborted, Guid? runIdFilter = null, int capacity = DefaultCapacity)
    {
        ConnectionId = Guid.NewGuid();
        RequestAborted = requestAborted;
        RunIdFilter = runIdFilter;
        _channel = Channel.CreateBounded<FlowLifecycleEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Stable identifier for this connection inside the broadcaster's registry.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Token cancelled when the underlying HTTP request ends.</summary>
    public CancellationToken RequestAborted { get; }

    /// <summary>Optional run-id filter; <see langword="null"/> means deliver every event.</summary>
    public Guid? RunIdFilter { get; }

    /// <summary>Reader side consumed by the SSE response loop.</summary>
    public ChannelReader<FlowLifecycleEvent> Reader => _channel.Reader;

    /// <summary>Attempts a non-blocking write of <paramref name="evt"/> to this connection's buffer.</summary>
    /// <returns><see langword="true"/> when the event was accepted (or replaced an older event under DropOldest); <see langword="false"/> when the channel is closed.</returns>
    public bool TryWrite(FlowLifecycleEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>Closes the channel so the reader loop terminates promptly.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
