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
        var page = await GetRunsPageAsync(flowId, null, skip, take, null);
        return page.Runs;
    }

    public async Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(
        Guid? flowId = null,
        string? status = null,
        int skip = 0,
        int take = 50,
        string? search = null)
    {
        await using var conn = new SqlConnection(_connectionString);
        var whereClauses = new List<string>();
        if (flowId.HasValue)
            whereClauses.Add("fr.FlowId = @FlowId");
        if (!string.IsNullOrWhiteSpace(status))
            whereClauses.Add("fr.Status = @Status");
        var searchLike = (string?)null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchLike = $"%{EscapeLikePattern(search)}%";
            whereClauses.Add("""
                (
                    CAST(fr.Id AS NVARCHAR(36)) LIKE @SearchLike ESCAPE '\'
                    OR ISNULL(fr.FlowName, '') LIKE @SearchLike ESCAPE '\'
                    OR ISNULL(fr.TriggerKey, '') LIKE @SearchLike ESCAPE '\'
                    OR ISNULL(fr.Status, '') LIKE @SearchLike ESCAPE '\'
                    OR ISNULL(fr.BackgroundJobId, '') LIKE @SearchLike ESCAPE '\'
                    OR EXISTS (
                        SELECT 1
                        FROM FlowSteps AS fs
                        WHERE fs.RunId = fr.Id
                          AND (
                                ISNULL(fs.StepKey, '') LIKE @SearchLike ESCAPE '\'
                                OR ISNULL(fs.ErrorMessage, '') LIKE @SearchLike ESCAPE '\'
                                OR ISNULL(fs.OutputJson, '') LIKE @SearchLike ESCAPE '\'
                              )
                    )
                )
                """);
        }

        var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var countSql = $"SELECT COUNT(*) FROM FlowRuns AS fr{whereSql}";
        var pageSql = "SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt FROM FlowRuns AS fr"
            + whereSql
            + " ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var parameters = new { FlowId = flowId, Status = status, Skip = skip, Take = take, SearchLike = searchLike };
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);
        var rows = await conn.QueryAsync<FlowRunRecord>(pageSql, parameters);

        return (rows.AsList(), totalCount);
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

    public async Task RetryStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            UPDATE FlowSteps
            SET Status = 'Running', OutputJson = NULL, ErrorMessage = NULL, CompletedAt = NULL, StartedAt = SYSDATETIMEOFFSET()
            WHERE RunId = @RunId AND StepKey = @StepKey
            """, new { RunId = runId, StepKey = stepKey });

        await conn.ExecuteAsync(
            "UPDATE FlowRuns SET Status = 'Running', CompletedAt = NULL WHERE Id = @Id",
            new { Id = runId });
    }

    private static async Task<FlowRunRecord?> GetRunCoreAsync(SqlConnection conn, Guid runId)
    {
        return await conn.QuerySingleOrDefaultAsync<FlowRunRecord>(
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt FROM FlowRuns WHERE Id = @Id",
            new { Id = runId });
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
    }
}
