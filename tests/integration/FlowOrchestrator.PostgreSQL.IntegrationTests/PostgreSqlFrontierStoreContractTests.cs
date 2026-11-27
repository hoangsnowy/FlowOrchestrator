using Dapper;
using FlowOrchestrator.Testing.Shared;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// <see cref="FrontierStoreContract"/> against a real PostgreSQL: the keyed status read, the open-row check
/// served by <c>ix_flow_steps_run_id_open</c>, the settled-iteration count, and the write-skew scenarios — two
/// concurrent completions of one join's prerequisites, a ForEach on concurrent workers — that depend on every
/// frontier read seeing all writes committed before it (#189 P0-1).
/// </summary>
public sealed class PostgreSqlFrontierStoreContractTests(PostgreSqlFixture fixture) : FrontierStoreContract, IClassFixture<PostgreSqlFixture>
{
    internal override FrontierStores CreateStores()
    {
        var store = new PostgreSqlFlowRunStore(fixture.ConnectionString);
        return new FrontierStores(store, store, store, new PostgreSqlOutputsRepository(fixture.ConnectionString));
    }

    [Fact]
    public async Task OpenRowIndex_servesTheOpenRowPredicate()
    {
        // Arrange - with sequential scans disabled the planner must reach the rows through an index; a plan
        // naming the partial index proves the store's predicate implies its WHERE clause.
        var runId = Guid.NewGuid();
        var store = new PostgreSqlFlowRunStore(fixture.ConnectionString);
        await store.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "T", null, null);
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SET enable_seqscan = off");

        // Act
        var plan = string.Join(Environment.NewLine, await conn.QueryAsync<string>(
            """
            EXPLAIN SELECT EXISTS (
                SELECT 1 FROM flow_steps
                WHERE run_id = @RunId AND status IN ('Running', 'Pending'))
            """,
            new { RunId = runId }));

        // Assert
        Assert.Contains("ix_flow_steps_run_id_open", plan, StringComparison.Ordinal);
    }
}
