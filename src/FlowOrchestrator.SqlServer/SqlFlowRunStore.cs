using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

public sealed class SqlFlowRunStore : IFlowRunStore
{
    private readonly string _connectionString;

    public SqlFlowRunStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt)
            VALUES (@Id, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, SYSDATETIMEOFFSET())
            """, new { Id = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId });

        return (await GetRunCoreAsync(conn, runId))!;
    }

    public async Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            MERGE FlowSteps AS target
            USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
            ON target.RunId = source.RunId AND target.StepKey = source.StepKey
            WHEN MATCHED THEN
                UPDATE SET StepType = @StepType, InputJson = @InputJson, JobId = @JobId, Status = 'Running', StartedAt = SYSDATETIMEOFFSET(), CompletedAt = NULL
            WHEN NOT MATCHED THEN
                INSERT (RunId, StepKey, StepType, Status, InputJson, JobId, StartedAt)
                VALUES (@RunId, @StepKey, @StepType, 'Running', @InputJson, @JobId, SYSDATETIMEOFFSET());
            """, new { RunId = runId, StepKey = stepKey, StepType = stepType, InputJson = inputJson, JobId = jobId });
    }

    public async Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            UPDATE FlowSteps
            SET Status = @Status, OutputJson = @OutputJson, ErrorMessage = @ErrorMessage, CompletedAt = SYSDATETIMEOFFSET()
            WHERE RunId = @RunId AND StepKey = @StepKey
            """, new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage });
    }

    public async Task CompleteRunAsync(Guid runId, string status)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowRuns SET Status = @Status, CompletedAt = SYSDATETIMEOFFSET() WHERE Id = @Id",
            new { Id = runId, Status = status });
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50)
    {
        await using var conn = new SqlConnection(_connectionString);
        var sql = "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt FROM FlowRuns";
        if (flowId.HasValue)
            sql += " WHERE FlowId = @FlowId";
        sql += " ORDER BY StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var rows = await conn.QueryAsync<FlowRunRecord>(sql, new { FlowId = flowId, Skip = skip, Take = take });
        return rows.AsList();
    }

    public async Task<FlowRunRecord?> GetRunDetailAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var run = await GetRunCoreAsync(conn, runId);
        if (run is null) return null;

        var steps = await conn.QueryAsync<FlowStepRecord>(
            "SELECT RunId, StepKey, StepType, Status, InputJson, OutputJson, ErrorMessage, JobId, StartedAt, CompletedAt FROM FlowSteps WHERE RunId = @RunId ORDER BY StartedAt",
            new { RunId = runId });
        run.Steps = steps.AsList();
        return run;
    }

    public async Task<DashboardStatistics> GetStatisticsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var stats = new DashboardStatistics();
        stats.TotalFlows = await conn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT FlowId) FROM FlowRuns");
        stats.ActiveRuns = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Running'");
        stats.CompletedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Succeeded' AND CAST(CompletedAt AS DATE) = CAST(SYSDATETIMEOFFSET() AS DATE)");
        stats.FailedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Failed' AND CAST(CompletedAt AS DATE) = CAST(SYSDATETIMEOFFSET() AS DATE)");
        return stats;
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>(
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt FROM FlowRuns WHERE Status = 'Running' ORDER BY StartedAt DESC");
        return rows.AsList();
    }

    private static async Task<FlowRunRecord?> GetRunCoreAsync(SqlConnection conn, Guid runId)
    {
        return await conn.QuerySingleOrDefaultAsync<FlowRunRecord>(
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt FROM FlowRuns WHERE Id = @Id",
            new { Id = runId });
    }
}
