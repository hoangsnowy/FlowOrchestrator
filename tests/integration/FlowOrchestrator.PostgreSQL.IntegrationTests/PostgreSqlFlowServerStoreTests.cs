using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// The server registry (<see cref="PostgreSqlFlowServerStore"/>) and the attempt server id on a real PostgreSQL:
/// upsert / list / remove / purge, a re-run of the migration, and the <c>flow_step_attempts.server_id</c> round trip.
/// </summary>
/// <remarks>
/// Ageing a server is simulated by moving its <c>last_heartbeat_at</c> back on the database clock, so no test waits
/// for wall time. Every test uses server ids of its own; the class's tests run one after the other.
/// </remarks>
public sealed class PostgreSqlFlowServerStoreTests : IClassFixture<PostgreSqlFixture>
{
    private readonly string _connectionString;
    private readonly PostgreSqlFlowServerStore _store;

    public PostgreSqlFlowServerStoreTests(PostgreSqlFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
        _store = new PostgreSqlFlowServerStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task Upsert_inserts_then_updates_every_column_but_the_start_time()
    {
        // Arrange
        var id = NewId();
        await _store.UpsertServerAsync(Server(id, activeSteps: 1, flows: 2));
        var inserted = await FindAsync(id);
        await BackdateAsync(id, TimeSpan.FromMinutes(1));

        // Act
        await _store.UpsertServerAsync(Server(id, activeSteps: 5, flows: 3) with { StartedAt = DateTimeOffset.MinValue, Runtime = "ServiceBus" });
        var updated = await FindAsync(id);

        // Assert
        Assert.Equal(inserted.StartedAt.AddMinutes(-1), updated.StartedAt);
        Assert.True(updated.LastHeartbeatAt > updated.StartedAt);
        Assert.Equal(5, updated.ActiveStepCount);
        Assert.Equal(3, updated.FlowCount);
        Assert.Equal("ServiceBus", updated.Runtime);
        Assert.Equal("host-a", updated.MachineName);
        Assert.Equal(4242, updated.ProcessId);
        Assert.Equal("2.0.0+abc", updated.Version);
    }

    [Fact]
    public async Task GetServers_lists_the_most_recent_heartbeat_first()
    {
        // Arrange
        var older = NewId();
        var newer = NewId();
        await _store.UpsertServerAsync(Server(older));
        await _store.UpsertServerAsync(Server(newer));
        await BackdateAsync(older, TimeSpan.FromMinutes(2));

        // Act
        var servers = await _store.GetServersAsync();

        // Assert
        var ids = servers.Select(s => s.ServerId).ToList();
        Assert.True(ids.IndexOf(newer) < ids.IndexOf(older));
    }

    [Fact]
    public async Task Remove_reports_whether_a_row_was_removed()
    {
        // Arrange
        var id = NewId();
        await _store.UpsertServerAsync(Server(id));

        // Act
        var first = await _store.RemoveServerAsync(id);
        var second = await _store.RemoveServerAsync(id);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.DoesNotContain(await _store.GetServersAsync(), s => s.ServerId == id);
    }

    [Fact]
    public async Task Purge_removes_only_servers_silent_for_longer_than_the_age()
    {
        // Arrange
        var gone = NewId();
        var alive = NewId();
        await _store.UpsertServerAsync(Server(gone));
        await _store.UpsertServerAsync(Server(alive));
        await BackdateAsync(gone, TimeSpan.FromHours(2));
        await BackdateAsync(alive, TimeSpan.FromMinutes(30));

        // Act
        var removed = await _store.PurgeServersAsync(TimeSpan.FromHours(1));

        // Assert
        Assert.Equal(1, removed);
        var ids = (await _store.GetServersAsync()).Select(s => s.ServerId).ToList();
        Assert.DoesNotContain(gone, ids);
        Assert.Contains(alive, ids);
    }

    [Fact]
    public async Task Migration_runs_again_without_error_and_keeps_the_rows()
    {
        // Arrange
        var id = NewId();
        await _store.UpsertServerAsync(Server(id));
        var migrator = new PostgreSqlFlowOrchestratorMigrator(_connectionString, NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance);

        // Act
        await migrator.StartAsync(CancellationToken.None);
        await migrator.StartAsync(CancellationToken.None);
        await migrator.BackgroundIndexBuild;

        // Assert
        await using var conn = new NpgsqlConnection(_connectionString);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'flow_step_attempts' AND column_name = 'server_id'"));
        Assert.Contains(await _store.GetServersAsync(), s => s.ServerId == id);
    }

    [Fact]
    public async Task Attempt_server_id_round_trips_and_the_five_parameter_overload_records_none()
    {
        // Arrange
        var runs = new PostgreSqlFlowRunStore(_connectionString);
        var runId = Guid.NewGuid();
        await runs.StartRunAsync(Guid.NewGuid(), "ServerFlow", runId, "manual", null, null);

        // Act
        await runs.RecordStepStartAsync(runId, "a", "Work", null, "job-1", "host-a:4242:abc");
        await runs.RecordStepCompleteAsync(runId, "a", "Failed", null, "boom");
        await runs.RecordStepStartAsync(runId, "a", "Work", null, "job-2");
        var detail = await runs.GetRunDetailAsync(runId);

        // Assert
        var attempts = Assert.Single(detail!.Steps!).Attempts!;
        Assert.Equal(2, attempts.Count);
        Assert.Equal("host-a:4242:abc", attempts[0].ServerId);
        Assert.Null(attempts[1].ServerId);
    }

    private static string NewId() => $"host-a:4242:{Guid.NewGuid():N}";

    private static FlowServerRecord Server(string id, int activeSteps = 0, int flows = 1) => new()
    {
        ServerId = id,
        MachineName = "host-a",
        ProcessId = 4242,
        Runtime = "Hangfire",
        Version = "2.0.0+abc",
        ActiveStepCount = activeSteps,
        FlowCount = flows,
    };

    private async Task<FlowServerRecord> FindAsync(string id)
        => Assert.Single(await _store.GetServersAsync(), s => s.ServerId == id);

    private async Task BackdateAsync(string id, TimeSpan age)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_servers SET last_heartbeat_at = last_heartbeat_at - @Age, started_at = started_at - @Age WHERE server_id = @Id",
            new { Id = id, Age = age });
    }
}
