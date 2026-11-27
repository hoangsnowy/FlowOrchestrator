using Dapper;
using FlowOrchestrator.Testing.Shared;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer.Tests;

/// <summary>
/// <see cref="FrontierStoreContract"/> against a real SQL Server: the keyed status read, the open-row check
/// served by <c>IX_FlowSteps_RunId_Open</c>, the settled-iteration count, and the write-skew scenarios — two
/// concurrent completions of one join's prerequisites, a ForEach on concurrent workers — that depend on every
/// frontier read seeing all writes committed before it (#189 P0-1).
/// </summary>
public sealed class SqlFrontierStoreContractTests(SqlServerFixture fixture) : FrontierStoreContract, IClassFixture<SqlServerFixture>
{
    internal override FrontierStores CreateStores()
    {
        var store = new SqlFlowRunStore(fixture.ConnectionString);
        return new FrontierStores(store, store, store, new SqlOutputsRepository(fixture.ConnectionString));
    }

    [Fact]
    public async Task OpenRowIndex_servesTheOpenRowPredicate()
    {
        // Arrange - an index hint fails the statement (error 8622) when the filtered index cannot answer the
        // predicate, so executing it proves the store's literal filter matches IX_FlowSteps_RunId_Open.
        var runId = Guid.NewGuid();
        var store = new SqlFlowRunStore(fixture.ConnectionString);
        await store.StartRunAsync(Guid.NewGuid(), "Frontier", runId, "manual", null, null);
        await store.RecordStepStartAsync(runId, "a", "T", null, null);
        await using var conn = new SqlConnection(fixture.ConnectionString);

        // Act
        var open = await conn.ExecuteScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM FlowSteps WITH (INDEX(IX_FlowSteps_RunId_Open))
                WHERE RunId = @RunId AND [Status] IN (N'Running', N'Pending')) THEN 1 ELSE 0 END
            """,
            new { RunId = runId });

        // Assert
        Assert.Equal(1, open);
    }
}
