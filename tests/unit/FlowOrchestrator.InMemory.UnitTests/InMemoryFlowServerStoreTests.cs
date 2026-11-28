using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryFlowServerStore"/> upsert / list / remove / purge semantics, and the server id the in-memory
/// run store keeps on an attempt.
/// </summary>
public sealed class InMemoryFlowServerStoreTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFlowServerStore _sut;

    public InMemoryFlowServerStoreTests()
    {
        _sut = new InMemoryFlowServerStore(_clock);
    }

    [Fact]
    public async Task Upsert_inserts_then_updates_keeping_the_start_time()
    {
        // Arrange
        var started = _clock.GetUtcNow();
        await _sut.UpsertServerAsync(Server("s1", activeSteps: 1));
        _clock.Advance(TimeSpan.FromSeconds(15));

        // Act
        await _sut.UpsertServerAsync(Server("s1", activeSteps: 4) with { StartedAt = DateTimeOffset.MinValue });
        var servers = await _sut.GetServersAsync();

        // Assert
        var server = Assert.Single(servers);
        Assert.Equal(started, server.StartedAt);
        Assert.Equal(started.AddSeconds(15), server.LastHeartbeatAt);
        Assert.Equal(4, server.ActiveStepCount);
        Assert.Equal("Hangfire", server.Runtime);
    }

    [Fact]
    public async Task GetServers_lists_the_most_recent_heartbeat_first()
    {
        // Arrange
        await _sut.UpsertServerAsync(Server("old"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _sut.UpsertServerAsync(Server("new"));

        // Act
        var servers = await _sut.GetServersAsync();

        // Assert
        Assert.Equal(["new", "old"], servers.Select(s => s.ServerId));
    }

    [Fact]
    public async Task Remove_reports_whether_a_row_was_removed()
    {
        // Arrange
        await _sut.UpsertServerAsync(Server("s1"));

        // Act
        var first = await _sut.RemoveServerAsync("s1");
        var second = await _sut.RemoveServerAsync("s1");

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.Empty(await _sut.GetServersAsync());
    }

    [Fact]
    public async Task Purge_removes_only_servers_silent_for_longer_than_the_age()
    {
        // Arrange
        await _sut.UpsertServerAsync(Server("gone"));
        _clock.Advance(TimeSpan.FromMinutes(50));
        await _sut.UpsertServerAsync(Server("alive"));
        _clock.Advance(TimeSpan.FromMinutes(11));

        // Act
        var removed = await _sut.PurgeServersAsync(TimeSpan.FromHours(1));

        // Assert
        Assert.Equal(1, removed);
        Assert.Equal("alive", Assert.Single(await _sut.GetServersAsync()).ServerId);
    }

    [Fact]
    public async Task Purge_rejects_a_non_positive_age()
    {
        // Arrange

        // Act
        var ex = await Record.ExceptionAsync(() => _sut.PurgeServersAsync(TimeSpan.Zero));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public async Task Run_store_keeps_the_server_id_on_the_attempt_and_the_five_parameter_overload_records_none()
    {
        // Arrange
        var runs = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();
        await runs.StartRunAsync(Guid.NewGuid(), "Flow", runId, "manual", null, null);

        // Act
        await runs.RecordStepStartAsync(runId, "a", "Work", null, "job-1", "host:1:abc");
        await runs.RecordStepCompleteAsync(runId, "a", "Failed", null, "boom");
        await runs.RecordStepStartAsync(runId, "a", "Work", null, "job-2");
        var detail = await runs.GetRunDetailAsync(runId);

        // Assert
        var attempts = Assert.Single(detail!.Steps!).Attempts!;
        Assert.Equal(2, attempts.Count);
        Assert.Equal("host:1:abc", attempts[0].ServerId);
        Assert.Null(attempts[1].ServerId);
    }

    private static FlowServerRecord Server(string id, int activeSteps = 0) => new()
    {
        ServerId = id,
        MachineName = "host",
        ProcessId = 42,
        Runtime = "Hangfire",
        Version = "2.0.0",
        ActiveStepCount = activeSteps,
        FlowCount = 3,
    };

    [Fact]
    public async Task GetServers_reports_the_clock_it_read_the_rows_with()
    {
        // Arrange
        await _sut.UpsertServerAsync(Server("s1", activeSteps: 0));
        _clock.Advance(TimeSpan.FromSeconds(20));

        // Act
        var server = Assert.Single(await _sut.GetServersAsync());

        // Assert
        Assert.Equal(_clock.GetUtcNow(), server.ObservedAt);
    }
}
