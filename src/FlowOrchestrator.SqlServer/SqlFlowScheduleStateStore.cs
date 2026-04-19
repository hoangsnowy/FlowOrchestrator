using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IFlowScheduleStateStore"/> that persists cron overrides
/// and pause state in the <c>FlowScheduleStates</c> table using MERGE (upsert) semantics.
/// </summary>
public sealed class SqlFlowScheduleStateStore : IFlowScheduleStateStore
{
    private readonly string _connectionString;

    /// <summary>Initialises the store with the given SQL Server connection string.</summary>
    /// <param name="connectionString">SQL Server connection string for the FlowOrchestrator database.</param>
    public SqlFlowScheduleStateStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowScheduleState?> GetAsync(string jobId)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowScheduleState>(
            """
            SELECT JobId, FlowId, FlowName, TriggerKey, IsPaused, CronOverride, UpdatedAtUtc
            FROM FlowScheduleStates
            WHERE JobId = @JobId
            """,
            new { JobId = jobId });
    }

    public async Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowScheduleState>(
            """
            SELECT JobId, FlowId, FlowName, TriggerKey, IsPaused, CronOverride, UpdatedAtUtc
            FROM FlowScheduleStates
            ORDER BY JobId
            """);
        return rows.AsList();
    }

    public async Task SaveAsync(FlowScheduleState state)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            MERGE FlowScheduleStates AS target
            USING (SELECT @JobId AS JobId) AS source
            ON target.JobId = source.JobId
            WHEN MATCHED THEN
                UPDATE SET
                    FlowId = @FlowId,
                    FlowName = @FlowName,
                    TriggerKey = @TriggerKey,
                    IsPaused = @IsPaused,
                    CronOverride = @CronOverride,
                    UpdatedAtUtc = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN
                INSERT (JobId, FlowId, FlowName, TriggerKey, IsPaused, CronOverride, UpdatedAtUtc)
                VALUES (@JobId, @FlowId, @FlowName, @TriggerKey, @IsPaused, @CronOverride, SYSDATETIMEOFFSET());
            """,
            state);
    }

    public async Task DeleteAsync(string jobId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM FlowScheduleStates WHERE JobId = @JobId", new { JobId = jobId });
    }
}

