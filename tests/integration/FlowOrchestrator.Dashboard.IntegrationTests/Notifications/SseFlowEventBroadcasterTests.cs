using FlowOrchestrator.Core.Notifications;
using FlowOrchestrator.Dashboard.Notifications;

namespace FlowOrchestrator.Dashboard.Tests.Notifications;

/// <summary>
/// In-memory tests for <see cref="SseFlowEventBroadcaster"/>: registry lifecycle,
/// per-connection filter, and the bounded-channel drop-oldest behaviour that protects
/// the broadcaster against a slow client.
/// </summary>
public sealed class SseFlowEventBroadcasterTests
{
    [Fact]
    public async Task Publish_with_no_connections_does_not_throw()
    {
        // Arrange
        var broadcaster = new SseFlowEventBroadcaster();
        var evt = new RunStartedEvent { RunId = Guid.NewGuid(), FlowId = Guid.NewGuid(), FlowName = "F", TriggerKey = "manual" };

        // Act
        await broadcaster.PublishAsync(evt);

        // Assert
        Assert.Equal(0, broadcaster.ConnectionCount);
    }

    [Fact]
    public async Task Publish_reaches_all_connected_subscribers()
    {
        // Arrange
        var broadcaster = new SseFlowEventBroadcaster();
        var c1 = new SseConnection(CancellationToken.None);
        var c2 = new SseConnection(CancellationToken.None);
        await using var r1 = broadcaster.Register(c1);
        await using var r2 = broadcaster.Register(c2);

        var evt = new RunCompletedEvent { RunId = Guid.NewGuid(), Status = "Succeeded" };

        // Act
        await broadcaster.PublishAsync(evt);

        // Assert — each connection's reader has exactly one item.
        Assert.True(c1.Reader.TryRead(out var got1));
        Assert.True(c2.Reader.TryRead(out var got2));
        Assert.Same(evt, got1);
        Assert.Same(evt, got2);
    }

    [Fact]
    public async Task RunIdFilter_excludes_unrelated_events()
    {
        // Arrange
        var matching = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        var broadcaster = new SseFlowEventBroadcaster();
        var conn = new SseConnection(CancellationToken.None, runIdFilter: matching);
        await using var registration = broadcaster.Register(conn);

        // Act — publish one matching, one unrelated.
        await broadcaster.PublishAsync(new RunStartedEvent
        {
            RunId = matching,
            FlowId = Guid.NewGuid(),
            FlowName = "F",
            TriggerKey = "manual"
        });
        await broadcaster.PublishAsync(new RunStartedEvent
        {
            RunId = unrelated,
            FlowId = Guid.NewGuid(),
            FlowName = "F",
            TriggerKey = "manual"
        });

        // Assert — only the matching event was buffered.
        Assert.True(conn.Reader.TryRead(out var first));
        Assert.NotNull(first);
        Assert.Equal(matching, first!.RunId);
        Assert.False(conn.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Bounded_channel_drops_oldest_when_capacity_exceeded()
    {
        // Arrange — capacity 4 so the test is fast and unambiguous.
        var broadcaster = new SseFlowEventBroadcaster();
        var conn = new SseConnection(CancellationToken.None, capacity: 4);
        await using var registration = broadcaster.Register(conn);

        // Act — publish 10 events without reading. The slow-reader scenario.
        for (var i = 0; i < 10; i++)
        {
            await broadcaster.PublishAsync(new RunCompletedEvent
            {
                RunId = Guid.NewGuid(),
                Status = "Succeeded"
            });
        }

        // Assert — exactly 4 items left (the newest 4); reader does NOT see the first 6.
        var drained = 0;
        while (conn.Reader.TryRead(out _)) drained++;
        Assert.Equal(4, drained);
    }

    [Fact]
    public async Task Registration_dispose_removes_connection_from_registry()
    {
        // Arrange
        var broadcaster = new SseFlowEventBroadcaster();
        var conn = new SseConnection(CancellationToken.None);
        var registration = broadcaster.Register(conn);
        Assert.Equal(1, broadcaster.ConnectionCount);

        // Act
        await registration.DisposeAsync();

        // Assert
        Assert.Equal(0, broadcaster.ConnectionCount);
    }

    [Fact]
    public async Task Many_connect_disconnect_cycles_do_not_leak()
    {
        // Arrange
        var broadcaster = new SseFlowEventBroadcaster();

        // Act
        for (var i = 0; i < 100; i++)
        {
            var conn = new SseConnection(CancellationToken.None);
            var registration = broadcaster.Register(conn);
            await registration.DisposeAsync();
        }

        // Assert
        Assert.Equal(0, broadcaster.ConnectionCount);
    }
}
