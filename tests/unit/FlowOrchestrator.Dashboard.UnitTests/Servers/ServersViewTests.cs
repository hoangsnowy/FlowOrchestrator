using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Servers;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.Dashboard.Tests.Servers;

/// <summary>
/// <see cref="ServersView"/>: the Active / Stale status against the configured threshold, the row order, and the
/// untracked payload. The clock is a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class ServersViewTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly FlowServerOptions _options = new(); // 15 s x 3 = stale after 45 s

    [Theory]
    [InlineData(0, ServersView.Active)]
    [InlineData(45, ServersView.Active)]   // exactly at the threshold: not yet stale
    [InlineData(46, ServersView.Stale)]
    [InlineData(-30, ServersView.Active)]  // heartbeat ahead of this clock (skew)
    public void StatusFor_is_stale_only_past_the_threshold(int secondsAgo, string expected)
    {
        // Arrange
        var now = _time.GetUtcNow();
        var lastHeartbeat = now.AddSeconds(-secondsAgo);

        // Act
        var status = ServersView.StatusFor(lastHeartbeat, now, _options.StaleAfter);

        // Assert
        Assert.Equal(expected, status);
    }

    [Fact]
    public void Build_follows_a_configured_threshold()
    {
        // Arrange
        var options = new FlowServerOptions { HeartbeatInterval = TimeSpan.FromSeconds(5), MissedHeartbeatsBeforeStale = 2 };
        var now = _time.GetUtcNow();

        // Act
        var view = ServersView.Build([Server("a", "m1", now.AddSeconds(-11))], now, options);

        // Assert
        Assert.Equal(ServersView.Stale, Assert.Single(view.Servers).Status);
        Assert.Equal(10, view.StaleAfterSeconds);
        Assert.Equal(5, view.HeartbeatIntervalSeconds);
    }

    [Fact]
    public void Build_lists_active_servers_first_then_by_machine_and_process()
    {
        // Arrange
        var now = _time.GetUtcNow();
        var servers = new[]
        {
            Server("stale", "a-host", now.AddMinutes(-5)),
            Server("b2", "b-host", now.AddSeconds(-1), processId: 2),
            Server("a1", "A-host", now.AddSeconds(-3)),
            Server("b1", "b-host", now.AddSeconds(-2), processId: 1),
        };

        // Act
        var view = ServersView.Build(servers, now, _options);

        // Assert
        Assert.True(view.Tracked);
        Assert.Equal(["a1", "b1", "b2", "stale"], view.Servers.Select(s => s.ServerId));
        Assert.Equal(now, view.ServerTime);
    }

    [Fact]
    public void Build_reports_whole_seconds_since_the_heartbeat_and_never_a_negative_value()
    {
        // Arrange
        var now = _time.GetUtcNow();

        // Act
        var view = ServersView.Build([Server("past", "m", now.AddSeconds(-90.7)), Server("future", "n", now.AddSeconds(10))], now, _options);

        // Assert
        Assert.Equal(90, view.Servers.Single(s => s.ServerId == "past").LastSeenSeconds);
        Assert.Equal(0, view.Servers.Single(s => s.ServerId == "future").LastSeenSeconds);
    }

    [Fact]
    public void Build_carries_every_recorded_column()
    {
        // Arrange
        var now = _time.GetUtcNow();
        var record = Server("id-1", "host", now) with { Runtime = "ServiceBus", Version = "2.0.0", ActiveStepCount = 7, FlowCount = 3, StartedAt = now.AddHours(-1) };

        // Act
        var row = Assert.Single(ServersView.Build([record], now, _options).Servers);

        // Assert
        Assert.Equal("id-1", row.ServerId);
        Assert.Equal("host", row.MachineName);
        Assert.Equal(1234, row.ProcessId);
        Assert.Equal("ServiceBus", row.Runtime);
        Assert.Equal("2.0.0", row.Version);
        Assert.Equal(7, row.ActiveStepCount);
        Assert.Equal(3, row.FlowCount);
        Assert.Equal(now.AddHours(-1), row.StartedAt);
        Assert.Equal(now, row.LastHeartbeatAt);
    }

    [Fact]
    public void NotTracked_is_an_empty_untracked_payload()
    {
        // Arrange
        var now = _time.GetUtcNow();

        // Act
        var view = ServersView.NotTracked(_options, now);

        // Assert
        Assert.False(view.Tracked);
        Assert.Empty(view.Servers);
        Assert.Equal(45, view.StaleAfterSeconds);
    }

    private static FlowServerRecord Server(string id, string machine, DateTimeOffset lastHeartbeat, int processId = 1234) => new()
    {
        ServerId = id,
        MachineName = machine,
        ProcessId = processId,
        Runtime = "Hangfire",
        StartedAt = lastHeartbeat,
        LastHeartbeatAt = lastHeartbeat,
    };

    [Theory]
    [InlineData(10)]   // dashboard host 10 min ahead of the database
    [InlineData(-10)]  // dashboard host 10 min behind the database
    public void Build_ages_a_heartbeat_by_the_store_clock_not_the_host_clock(int hostAheadMinutes)
    {
        // Arrange — heartbeat stamped by the database 5 s before the database read the list.
        var dbNow = _time.GetUtcNow();
        var server = Server("live", "m1", dbNow.AddSeconds(-5)) with { ObservedAt = dbNow };
        var hostNow = dbNow.AddMinutes(hostAheadMinutes);

        // Act
        var row = Assert.Single(ServersView.Build([server], hostNow, _options).Servers);

        // Assert
        Assert.Equal(ServersView.Active, row.Status);
        Assert.Equal(5, row.LastSeenSeconds);
    }

    [Fact]
    public void Build_ages_a_dead_server_by_the_store_clock_even_when_the_host_clock_is_behind()
    {
        // Arrange — the database last heard from it 10 min ago; the host clock lags the database by 10 min.
        var dbNow = _time.GetUtcNow();
        var server = Server("dead", "m1", dbNow.AddMinutes(-10)) with { ObservedAt = dbNow };

        // Act
        var row = Assert.Single(ServersView.Build([server], dbNow.AddMinutes(-10), _options).Servers);

        // Assert
        Assert.Equal(ServersView.Stale, row.Status);
    }
}
