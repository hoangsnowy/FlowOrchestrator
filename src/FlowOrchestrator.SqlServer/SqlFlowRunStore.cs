using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of all run storage interfaces.
/// A single class implements <see cref="IFlowRunStore"/>, <see cref="IFlowRunRuntimeStore"/>,
/// <see cref="IFlowRunControlStore"/>, and <see cref="IFlowRetentionStore"/> to share the connection string
/// and avoid multiple registrations.
/// Step claim deduplication uses a SQL <c>INSERT IF NOT EXISTS</c> pattern for atomicity.
/// </summary>
public sealed class SqlFlowRunStore :
    IFlowRunStore,
    IFlowRunRuntimeStore,
    IFlowRunControlStore,
    IFlowRetentionStore
{
    private readonly string _connectionString;

    public SqlFlowRunStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, SourceRunId)
            VALUES (@Id, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, SYSDATETIMEOFFSET(), @SourceRunId)
            """, new { Id = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId, SourceRunId = sourceRunId });

        return (await GetRunCoreAsync(conn, runId))!;
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetDerivedRunsAsync(Guid sourceRunId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>("""
            SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt, SourceRunId
            FROM FlowRuns
            WHERE SourceRunId = @SourceRunId
            ORDER BY StartedAt DESC
            """, new { SourceRunId = sourceRunId });
        return rows.AsList();
    }

    public async Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            BEGIN TRANSACTION;

            DECLARE @AttemptNo INT;
            SELECT @AttemptNo = ISNULL(MAX(AttemptNo), 0) + 1
            FROM FlowStepAttempts WITH (UPDLOCK, HOLDLOCK)
            WHERE RunId = @RunId AND StepKey = @StepKey;

            INSERT INTO FlowStepAttempts (RunId, StepKey, AttemptNo, StepType, Status, InputJson, JobId, StartedAt)
            VALUES (@RunId, @StepKey, @AttemptNo, @StepType, 'Running', @InputJson, @JobId, SYSDATETIMEOFFSET());

            MERGE FlowSteps AS target
            USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
            ON target.RunId = source.RunId AND target.StepKey = source.StepKey
            WHEN MATCHED THEN
                UPDATE SET
                    StepType = @StepType,
                    InputJson = @InputJson,
                    OutputJson = NULL,
                    ErrorMessage = NULL,
                    JobId = @JobId,
                    Status = 'Running',
                    StartedAt = SYSDATETIMEOFFSET(),
                    CompletedAt = NULL
            WHEN NOT MATCHED THEN
                INSERT (RunId, StepKey, StepType, Status, InputJson, JobId, StartedAt)
                VALUES (@RunId, @StepKey, @StepType, 'Running', @InputJson, @JobId, SYSDATETIMEOFFSET());

            COMMIT TRANSACTION;
            """, new { RunId = runId, StepKey = stepKey, StepType = stepType, InputJson = inputJson, JobId = jobId });
    }

    public async Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("""
            BEGIN TRANSACTION;

            UPDATE FlowSteps
            SET Status = @Status, OutputJson = @OutputJson, ErrorMessage = @ErrorMessage, CompletedAt = SYSDATETIMEOFFSET()
            WHERE RunId = @RunId AND StepKey = @StepKey

            UPDATE attempts
            SET Status = @Status, OutputJson = @OutputJson, ErrorMessage = @ErrorMessage, CompletedAt = SYSDATETIMEOFFSET()
            FROM FlowStepAttempts AS attempts
            INNER JOIN (
                SELECT TOP (1) RunId, StepKey, AttemptNo
                FROM FlowStepAttempts
                WHERE RunId = @RunId AND StepKey = @StepKey
                ORDER BY AttemptNo DESC
            ) AS latest
                ON latest.RunId = attempts.RunId
               AND latest.StepKey = attempts.StepKey
               AND latest.AttemptNo = attempts.AttemptNo

            COMMIT TRANSACTION;
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
                    OR EXISTS (
                        SELECT 1
                        FROM FlowStepAttempts AS fsa
                        WHERE fsa.RunId = fr.Id
                          AND (
                                ISNULL(fsa.StepKey, '') LIKE @SearchLike ESCAPE '\'
                                OR ISNULL(fsa.ErrorMessage, '') LIKE @SearchLike ESCAPE '\'
                                OR ISNULL(fsa.OutputJson, '') LIKE @SearchLike ESCAPE '\'
                              )
                    )
                )
                """);
        }

        var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var countSql = $"SELECT COUNT(*) FROM FlowRuns AS fr{whereSql}";
        var pageSql = "SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt, fr.SourceRunId FROM FlowRuns AS fr"
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

        var steps = (await conn.QueryAsync<FlowStepRecord>(
            "SELECT RunId, StepKey, StepType, Status, InputJson, OutputJson, ErrorMessage, JobId, StartedAt, CompletedAt, EvaluationTraceJson FROM FlowSteps WHERE RunId = @RunId ORDER BY StartedAt",
            new { RunId = runId }))
            .AsList();

        var attempts = (await conn.QueryAsync<FlowStepAttemptRecord>(
            "SELECT RunId, StepKey, AttemptNo AS Attempt, StepType, Status, InputJson, OutputJson, ErrorMessage, JobId, StartedAt, CompletedAt, EvaluationTraceJson FROM FlowStepAttempts WHERE RunId = @RunId ORDER BY StepKey, AttemptNo",
            new { RunId = runId })
            ).AsList();

        PopulateStepAttempts(steps, attempts);
        run.Steps = steps;
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
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt, SourceRunId FROM FlowRuns WHERE Status = 'Running' ORDER BY StartedAt DESC");
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

    public async Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.ExecuteAsync("""
            INSERT INTO FlowStepDispatches (RunId, StepKey)
            SELECT @RunId, @StepKey
            WHERE NOT EXISTS (
                SELECT 1 FROM FlowStepDispatches WHERE RunId = @RunId AND StepKey = @StepKey)
            """, new { RunId = runId, StepKey = stepKey });
        return rows > 0;
    }

    public async Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowStepDispatches SET DispatchJobId = @JobId WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey, JobId = jobId });
    }

    public async Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM FlowStepDispatches WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey });
    }

    public async Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var keys = await conn.QueryAsync<string>(
            "SELECT StepKey FROM FlowStepDispatches WHERE RunId = @RunId",
            new { RunId = runId });
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string StepKey, string Status)>(
            "SELECT StepKey, Status FROM FlowSteps WHERE RunId = @RunId",
            new { RunId = runId });

        return rows.ToDictionary(
            x => x.StepKey,
            x => ParseStepStatus(x.Status),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<string>(
            "SELECT StepKey FROM FlowStepClaims WHERE RunId = @RunId",
            new { RunId = runId });

        return rows.AsList();
    }

    public async Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        var affected = await conn.ExecuteScalarAsync<int>(
            """
            MERGE FlowStepClaims AS target
            USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
            ON target.RunId = source.RunId AND target.StepKey = source.StepKey
            WHEN NOT MATCHED THEN
                INSERT (RunId, StepKey, ClaimedAt) VALUES (@RunId, @StepKey, SYSDATETIMEOFFSET());
            SELECT @@ROWCOUNT;
            """,
            new { RunId = runId, StepKey = stepKey });

        return affected > 0;
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
    {
        await RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson: null).ConfigureAwait(false);
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
    {
        await RecordStepStartAsync(runId, stepKey, stepType, null, null).ConfigureAwait(false);
        await RecordStepCompleteAsync(runId, stepKey, StepStatus.Skipped.ToString(), null, reason).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(evaluationTraceJson))
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                """
                BEGIN TRANSACTION;

                UPDATE FlowSteps
                SET EvaluationTraceJson = @EvaluationTraceJson
                WHERE RunId = @RunId AND StepKey = @StepKey;

                UPDATE attempts
                SET EvaluationTraceJson = @EvaluationTraceJson
                FROM FlowStepAttempts AS attempts
                INNER JOIN (
                    SELECT TOP (1) RunId, StepKey, AttemptNo
                    FROM FlowStepAttempts
                    WHERE RunId = @RunId AND StepKey = @StepKey
                    ORDER BY AttemptNo DESC
                ) AS latest
                    ON latest.RunId = attempts.RunId
                   AND latest.StepKey = attempts.StepKey
                   AND latest.AttemptNo = attempts.AttemptNo;

                COMMIT TRANSACTION;
                """,
                new { RunId = runId, StepKey = stepKey, EvaluationTraceJson = evaluationTraceJson }).ConfigureAwait(false);
        }
    }

    public async Task<string?> GetRunStatusAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT Status FROM FlowRuns WHERE Id = @Id",
            new { Id = runId });
    }

    public async Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            MERGE FlowRunControls AS target
            USING (SELECT @RunId AS RunId) AS source
            ON target.RunId = source.RunId
            WHEN MATCHED THEN
                UPDATE SET
                    FlowId = @FlowId,
                    TriggerKey = @TriggerKey,
                    IdempotencyKey = @IdempotencyKey,
                    TimeoutAtUtc = @TimeoutAtUtc
            WHEN NOT MATCHED THEN
                INSERT (RunId, FlowId, TriggerKey, IdempotencyKey, TimeoutAtUtc, CancelRequested)
                VALUES (@RunId, @FlowId, @TriggerKey, @IdempotencyKey, @TimeoutAtUtc, 0);
            """,
            new
            {
                RunId = runId,
                FlowId = flowId,
                TriggerKey = triggerKey,
                IdempotencyKey = idempotencyKey,
                TimeoutAtUtc = timeoutAtUtc
            });
    }

    public async Task<FlowRunControlRecord?> GetRunControlAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowRunControlRecord>(
            """
            SELECT
                RunId,
                FlowId,
                TriggerKey,
                IdempotencyKey,
                TimeoutAtUtc,
                CancelRequested,
                CancelReason,
                CancelRequestedAtUtc,
                TimedOutAtUtc
            FROM FlowRunControls
            WHERE RunId = @RunId
            """,
            new { RunId = runId });
    }

    public async Task<bool> RequestCancelAsync(Guid runId, string? reason)
    {
        await using var conn = new SqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE FlowRunControls
            SET CancelRequested = 1,
                CancelReason = @Reason,
                CancelRequestedAtUtc = COALESCE(CancelRequestedAtUtc, SYSDATETIMEOFFSET())
            WHERE RunId = @RunId
              AND CancelRequested = 0
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    public async Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
    {
        await using var conn = new SqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE FlowRunControls
            SET TimedOutAtUtc = COALESCE(TimedOutAtUtc, SYSDATETIMEOFFSET()),
                CancelRequested = 1,
                CancelReason = COALESCE(@Reason, CancelReason, 'Run timed out.'),
                CancelRequestedAtUtc = COALESCE(CancelRequestedAtUtc, SYSDATETIMEOFFSET())
            WHERE RunId = @RunId
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    public async Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = idempotencyKey.Trim().ToLowerInvariant();

        var runId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT RunId
            FROM FlowIdempotencyKeys
            WHERE FlowId = @FlowId
              AND TriggerKey = @TriggerKey
              AND IdempotencyKey = @IdempotencyKey
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = normalizedTriggerKey,
                IdempotencyKey = normalizedIdempotencyKey
            });

        return runId;
    }

    public async Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = idempotencyKey.Trim().ToLowerInvariant();

        var affected = await conn.ExecuteScalarAsync<int>(
            """
            MERGE FlowIdempotencyKeys AS target
            USING (
                SELECT @FlowId AS FlowId, @TriggerKey AS TriggerKey, @IdempotencyKey AS IdempotencyKey
            ) AS source
            ON target.FlowId = source.FlowId
               AND target.TriggerKey = source.TriggerKey
               AND target.IdempotencyKey = source.IdempotencyKey
            WHEN NOT MATCHED THEN
                INSERT (FlowId, TriggerKey, IdempotencyKey, RunId, CreatedAtUtc)
                VALUES (@FlowId, @TriggerKey, @IdempotencyKey, @RunId, SYSDATETIMEOFFSET());
            SELECT @@ROWCOUNT;
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = normalizedTriggerKey,
                IdempotencyKey = normalizedIdempotencyKey,
                RunId = runId
            });

        return affected > 0;
    }

    public async Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            DELETE fsc
            FROM FlowStepClaims fsc
            INNER JOIN FlowRuns fr ON fr.Id = fsc.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE frc
            FROM FlowRunControls frc
            INNER JOIN FlowRuns fr ON fr.Id = frc.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fio
            FROM FlowIdempotencyKeys fio
            INNER JOIN FlowRuns fr ON fr.Id = fio.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fo
            FROM FlowOutputs fo
            INNER JOIN FlowRuns fr ON fr.Id = fo.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fe
            FROM FlowEvents fe
            INNER JOIN FlowRuns fr ON fr.Id = fe.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fsa
            FROM FlowStepAttempts fsa
            INNER JOIN FlowRuns fr ON fr.Id = fsa.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fs
            FROM FlowSteps fs
            INNER JOIN FlowRuns fr ON fr.Id = fs.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE FROM FlowRuns
            WHERE CompletedAt IS NOT NULL
              AND CompletedAt < @CutoffUtc;
            """,
            new { CutoffUtc = cutoffUtc },
            tx).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FlowRunRecord?> GetRunCoreAsync(SqlConnection conn, Guid runId)
    {
        return await conn.QuerySingleOrDefaultAsync<FlowRunRecord>(
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt, SourceRunId FROM FlowRuns WHERE Id = @Id",
            new { Id = runId });
    }

    private static void PopulateStepAttempts(IReadOnlyList<FlowStepRecord> steps, IReadOnlyList<FlowStepAttemptRecord> attempts)
    {
        var attemptsLookup = attempts
            .GroupBy(a => a.StepKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FlowStepAttemptRecord>)g.OrderBy(a => a.Attempt).ToList(), StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (!attemptsLookup.TryGetValue(step.StepKey, out var stepAttempts) || stepAttempts.Count == 0)
            {
                stepAttempts = [CreateSyntheticAttempt(step)];
            }

            step.Attempts = stepAttempts;
            step.AttemptCount = stepAttempts.Count;
        }
    }

    private static FlowStepAttemptRecord CreateSyntheticAttempt(FlowStepRecord step)
    {
        return new FlowStepAttemptRecord
        {
            RunId = step.RunId,
            StepKey = step.StepKey,
            Attempt = 1,
            StepType = step.StepType,
            Status = step.Status,
            InputJson = step.InputJson,
            OutputJson = step.OutputJson,
            ErrorMessage = step.ErrorMessage,
            JobId = step.JobId,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
            EvaluationTraceJson = step.EvaluationTraceJson
        };
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
    }

    private static StepStatus ParseStepStatus(string status)
    {
        if (Enum.TryParse<StepStatus>(status, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return StepStatus.Failed;
    }
}
