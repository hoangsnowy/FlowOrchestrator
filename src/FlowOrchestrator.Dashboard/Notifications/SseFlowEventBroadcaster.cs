using System.Collections.Concurrent;
using FlowOrchestrator.Core.Notifications;

namespace FlowOrchestrator.Dashboard.Notifications;

/// <summary>
/// In-process <see cref="IFlowEventNotifier"/> implementation that fans <see cref="FlowLifecycleEvent"/>
/// values out to every registered <see cref="SseConnection"/>. Used by the dashboard's SSE
/// endpoint to push realtime updates to connected browsers.
/// </summary>
/// <remarks>
/// <para>
/// Single-process only. A multi-replica deployment must layer a backplane (e.g. Service Bus
/// topic re-publishing into the local broadcaster) on top — the <see cref="IFlowEventNotifier"/>
/// interface is preserved so the engine code stays unchanged.
/// </para>
/// <para>
/// <see cref="PublishAsync"/> is <b>non-blocking</b>: it iterates the registry, applies each
/// connection's filter, and calls <see cref="SseConnection.TryWrite"/>. A slow client never
/// stalls the engine; instead, its connection's bounded channel drops the oldest events.
/// </para>
/// </remarks>
public sealed class SseFlowEventBroadcaster : IFlowEventNotifier
{
    private readonly ConcurrentDictionary<Guid, SseConnection> _connections = new();

    /// <summary>Number of currently registered connections; useful for tests and diagnostics.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Registers <paramref name="connection"/> for fan-out and returns a disposable that removes
    /// it on dispose. Always pair with <c>await using</c> from the SSE endpoint handler.
    /// </summary>
    public IAsyncDisposable Register(SseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connections[connection.ConnectionId] = connection;
        return new Registration(this, connection);
    }

    /// <inheritdoc/>
    public ValueTask PublishAsync(FlowLifecycleEvent evt, CancellationToken ct = default)
    {
        if (evt is null)
        {
            return ValueTask.CompletedTask;
        }

        // Snapshot is unnecessary — ConcurrentDictionary enumeration is safe and we tolerate
        // a connection registering or disposing mid-publish (it just gets the next event or none).
        foreach (var pair in _connections)
        {
            var conn = pair.Value;
            if (conn.RunIdFilter is { } filter && filter != evt.RunId)
            {
                continue;
            }

            conn.TryWrite(evt);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Registration : IAsyncDisposable
    {
        private readonly SseFlowEventBroadcaster _owner;
        private readonly SseConnection _connection;
        private int _disposed;

        public Registration(SseFlowEventBroadcaster owner, SseConnection connection)
        {
            _owner = owner;
            _connection = connection;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner._connections.TryRemove(_connection.ConnectionId, out _);
                _connection.Complete();
            }
            return ValueTask.CompletedTask;
        }
    }
}
