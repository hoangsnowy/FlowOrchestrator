using System.Data;
using Dapper;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of all run storage interfaces.
/// Uses PostgreSQL's <c>INSERT ... ON CONFLICT DO NOTHING</c> for atomic step claim deduplication.
/// </summary>
public sealed class PostgreSqlFlowRunStore :
    IFlowRunStore,
    IFlowRunRuntimeStore,
    IFlowRunControlStore,
    IFlowRetentionStore
{
    private readonly string _connectionString;

    public PostgreSqlFlowRunStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_runs (id, flow_id, flow_name, status, trigger_key, trigger_data_json, background_job_id, started_at)
            VALUES (@Id, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, NOW())
            """,
            new { Id = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId });

        return (await GetRunCoreAsync(conn, runId))!;
    }

    public async Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable);

        var attemptNo = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(attempt_no), 0) + 1 FROM flow_step_attempts WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_attempts (run_id, step_key, attempt_no, step_type, status, input_json, job_id, started_at)
            VALUES (@RunId, @StepKey, @AttemptNo, @StepType, 'Running', @InputJson, @JobId, NOW())
            """,
            new { RunId = runId, StepKey = stepKey, AttemptNo = attemptNo, StepType = stepType, InputJson = inputJson, JobId = jobId }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO flow_steps (run_id, step_key, step_type, status, input_json, output_json, error_message, job_id, started_at, completed_at)
            VALUES (@RunId, @StepKey, @StepType, 'Running', @InputJson, NULL, NULL, @JobId, NOW(), NULL)
            ON CONFLICT (run_id, step_key) DO UPDATE SET
                step_type     = EXCLUDED.step_type,
                input_json    = EXCLUDED.input_json,
                output_json   = NULL,
                error_message = NULL,
                job_id        = EXCLUDED.job_id,
                status        = 'Running',
                started_at    = NOW(),
                completed_at  = NULL
            """,
            new { RunId = runId, StepKey = stepKey, StepType = stepType, InputJson = inputJson, JobId = jobId }, tx);

        await tx.CommitAsync();
    }

    public async Task RecordStepCompleteAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await conn.ExecuteAsync(
            """
            UPDATE flow_steps
            SET status = @Status, output_json = @OutputJson, error_message = @ErrorMessage, completed_at = NOW()
            WHERE run_id = @RunId AND step_key = @StepKey
            """,
            new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage }, tx);

        await conn.ExecuteAsync(
            """
            UPDATE flow_step_attempts fsa
            SET status = @Status, output_json = @OutputJson, error_message = @ErrorMessage, completed_at = NOW()
            FROM (
                SELECT attempt_no
                FROM flow_step_attempts
                WHERE run_id = @RunId AND step_key = @StepKey
                ORDER BY attempt_no DESC
                LIMIT 1
            ) AS latest
            WHERE fsa.run_id = @RunId AND fsa.step_key = @StepKey AND fsa.attempt_no = latest.attempt_no
            """,
            new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage }, tx);

        await tx.CommitAsync();
    }

    public async Task CompleteRunAsync(Guid runId, string status)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_runs SET status = @Status, completed_at = NOW() WHERE id = @Id",
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
        await using var conn = new NpgsqlConnection(_connectionString);
        var whereClauses = new List<string>();
        if (flowId.HasValue)
            whereClauses.Add("fr.flow_id = @FlowId");
        if (!string.IsNullOrWhiteSpace(status))
            whereClauses.Add("fr.status = @Status");

        var searchLike = (string?)null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchLike = $"%{EscapeLikePattern(search)}%";
            whereClauses.Add(
                """
                (
                    fr.id::text ILIKE @SearchLike
                    OR COALESCE(fr.flow_name, '') ILIKE @SearchLike
                    OR COALESCE(fr.trigger_key, '') ILIKE @SearchLike
                    OR COALESCE(fr.status, '') ILIKE @SearchLike
                    OR COALESCE(fr.background_job_id, '') ILIKE @SearchLike
                    OR EXISTS (
                        SELECT 1
                        FROM flow_steps AS fs
                        WHERE fs.run_id = fr.id
                          AND (
                                COALESCE(fs.step_key, '') ILIKE @SearchLike
                                OR COALESCE(fs.error_message, '') ILIKE @SearchLike
                                OR COALESCE(fs.output_json, '') ILIKE @SearchLike
                              )
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM flow_step_attempts AS fsa
                        WHERE fsa.run_id = fr.id
                          AND (
                                COALESCE(fsa.step_key, '') ILIKE @SearchLike
                                OR COALESCE(fsa.error_message, '') ILIKE @SearchLike
                                OR COALESCE(fsa.output_json, '') ILIKE @SearchLike
                              )
                    )
                )
                """);
        }

        var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        var countSql = $"SELECT COUNT(*) FROM flow_runs AS fr{whereSql}";
        var pageSql = $"""
            SELECT fr.id AS "Id", fr.flow_id AS "FlowId", fr.flow_name AS "FlowName",
                   fr.status AS "Status", fr.trigger_key AS "TriggerKey",
                   fr.trigger_data_json AS "TriggerDataJson", fr.background_job_id AS "BackgroundJobId",
                   fr.started_at AS "StartedAt", fr.completed_at AS "CompletedAt"
            FROM flow_runs AS fr
            """ + whereSql + """
             ORDER BY fr.started_at DESC
            LIMIT @Take OFFSET @Skip
            """;

        var parameters = new { FlowId = flowId, Status = status, Skip = skip, Take = take, SearchLike = searchLike };
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);
        var rows = await conn.QueryAsync<FlowRunRecord>(pageSql, parameters);

        return (rows.AsList(), totalCount);
    }

    public async Task<FlowRunRecord?> GetRunDetailAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var run = await GetRunCoreAsync(conn, runId);
        if (run is null) return null;

        var steps = (await conn.QueryAsync<FlowStepRecord>(
            """
            SELECT run_id AS "RunId", step_key AS "StepKey", step_type AS "StepType",
                   status AS "Status", input_json AS "InputJson", output_json AS "OutputJson",
                   error_message AS "ErrorMessage", job_id AS "JobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt"
            FROM flow_steps
            WHERE run_id = @RunId
            ORDER BY started_at
            """,
            new { RunId = runId }))
            .AsList();

        var attempts = (await conn.QueryAsync<FlowStepAttemptRecord>(
            """
            SELECT run_id AS "RunId", step_key AS "StepKey", attempt_no AS "Attempt",
                   step_type AS "StepType", status AS "Status",
                   input_json AS "InputJson", output_json AS "OutputJson",
                   error_message AS "ErrorMessage", job_id AS "JobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt"
            FROM flow_step_attempts
            WHERE run_id = @RunId
            ORDER BY step_key, attempt_no
            """,
            new { RunId = runId }))
            .AsList();

        PopulateStepAttempts(steps, attempts);
        run.Steps = steps;
        return run;
    }

    public async Task<DashboardStatistics> GetStatisticsAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var stats = new DashboardStatistics();
        stats.TotalFlows = await conn.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT flow_id) FROM flow_runs");
        stats.ActiveRuns = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM flow_runs WHERE status = 'Running'");
        stats.CompletedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow_runs WHERE status = 'Succeeded' AND completed_at::date = CURRENT_DATE");
        stats.FailedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow_runs WHERE status = 'Failed' AND completed_at::date = CURRENT_DATE");
        return stats;
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>(
            """
            SELECT id AS "Id", flow_id AS "FlowId", flow_name AS "FlowName",
                   status AS "Status", trigger_key AS "TriggerKey",
                   trigger_data_json AS "TriggerDataJson", background_job_id AS "BackgroundJobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt"
            FROM flow_runs
            WHERE status = 'Running'
            ORDER BY started_at DESC
            """);
        return rows.AsList();
    }

    public async Task RetryStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            UPDATE flow_steps
            SET status = 'Running', output_json = NULL, error_message = NULL,
                completed_at = NULL, started_at = NOW()
            WHERE run_id = @RunId AND step_key = @StepKey
            """,
            new { RunId = runId, StepKey = stepKey });

        await conn.ExecuteAsync(
            "UPDATE flow_runs SET status = 'Running', completed_at = NULL WHERE id = @Id",
            new { Id = runId });
    }

    public async Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_dispatches (run_id, step_key)
            VALUES (@RunId, @StepKey)
            ON CONFLICT (run_id, step_key) DO NOTHING
            """,
            new { RunId = runId, StepKey = stepKey });
        return rows > 0;
    }

    public async Task AnnotateDispatchAsync(Guid runId, string stepKey, string jobId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_step_dispatches SET dispatch_job_id = @JobId WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey, JobId = jobId });
    }

    public async Task ReleaseDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM flow_step_dispatches WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey });
    }

    public async Task<IReadOnlySet<string>> GetDispatchedStepKeysAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var keys = await conn.QueryAsync<string>(
            "SELECT step_key FROM flow_step_dispatches WHERE run_id = @RunId",
            new { RunId = runId });
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string StepKey, string Status)>(
            """
            SELECT step_key AS "StepKey", status AS "Status"
            FROM flow_steps
            WHERE run_id = @RunId
            """,
            new { RunId = runId });

        return rows.ToDictionary(
            x => x.StepKey,
            x => ParseStepStatus(x.Status),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<string>(
            "SELECT step_key FROM flow_step_claims WHERE run_id = @RunId",
            new { RunId = runId });

        return rows.AsList();
    }

    public async Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_claims (run_id, step_key, claimed_at)
            VALUES (@RunId, @StepKey, NOW())
            ON CONFLICT (run_id, step_key) DO NOTHING
            """,
            new { RunId = runId, StepKey = stepKey });

        return affected > 0;
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
    {
        await RecordStepStartAsync(runId, stepKey, stepType, null, null).ConfigureAwait(false);
        await RecordStepCompleteAsync(runId, stepKey, StepStatus.Skipped.ToString(), null, reason).ConfigureAwait(false);
    }

    public async Task<string?> GetRunStatusAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM flow_runs WHERE id = @Id",
            new { Id = runId });
    }

    public async Task ConfigureRunAsync(Guid runId, Guid flowId, string triggerKey, string? idempotencyKey, DateTimeOffset? timeoutAtUtc)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_run_controls
                (run_id, flow_id, trigger_key, idempotency_key, timeout_at_utc, cancel_requested)
            VALUES
                (@RunId, @FlowId, @TriggerKey, @IdempotencyKey, @TimeoutAtUtc, FALSE)
            ON CONFLICT (run_id) DO UPDATE SET
                flow_id = EXCLUDED.flow_id,
                trigger_key = EXCLUDED.trigger_key,
                idempotency_key = EXCLUDED.idempotency_key,
                timeout_at_utc = EXCLUDED.timeout_at_utc
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
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowRunControlRecord>(
            """
            SELECT
                run_id AS "RunId",
                flow_id AS "FlowId",
                trigger_key AS "TriggerKey",
                idempotency_key AS "IdempotencyKey",
                timeout_at_utc AS "TimeoutAtUtc",
                cancel_requested AS "CancelRequested",
                cancel_reason AS "CancelReason",
                cancel_requested_at_utc AS "CancelRequestedAtUtc",
                timed_out_at_utc AS "TimedOutAtUtc"
            FROM flow_run_controls
            WHERE run_id = @RunId
            """,
            new { RunId = runId });
    }

    public async Task<bool> RequestCancelAsync(Guid runId, string? reason)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE flow_run_controls
            SET cancel_requested = TRUE,
                cancel_reason = @Reason,
                cancel_requested_at_utc = COALESCE(cancel_requested_at_utc, NOW())
            WHERE run_id = @RunId
              AND cancel_requested = FALSE
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    public async Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE flow_run_controls
            SET timed_out_at_utc = COALESCE(timed_out_at_utc, NOW()),
                cancel_requested = TRUE,
                cancel_reason = COALESCE(@Reason, cancel_reason, 'Run timed out.'),
                cancel_requested_at_utc = COALESCE(cancel_requested_at_utc, NOW())
            WHERE run_id = @RunId
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    public async Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = idempotencyKey.Trim().ToLowerInvariant();

        return await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT run_id
            FROM flow_idempotency_keys
            WHERE flow_id = @FlowId
              AND trigger_key = @TriggerKey
              AND idempotency_key = @IdempotencyKey
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = normalizedTriggerKey,
                IdempotencyKey = normalizedIdempotencyKey
            });
    }

    public async Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = idempotencyKey.Trim().ToLowerInvariant();

        var affected = await conn.ExecuteAsync(
            """
            INSERT INTO flow_idempotency_keys (flow_id, trigger_key, idempotency_key, run_id, created_at_utc)
            VALUES (@FlowId, @TriggerKey, @IdempotencyKey, @RunId, NOW())
            ON CONFLICT (flow_id, trigger_key, idempotency_key) DO NOTHING
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            DELETE FROM flow_step_claims fsc
            USING flow_runs fr
            WHERE fr.id = fsc.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_run_controls frc
            USING flow_runs fr
            WHERE fr.id = frc.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_idempotency_keys fik
            USING flow_runs fr
            WHERE fr.id = fik.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_outputs fo
            USING flow_runs fr
            WHERE fr.id = fo.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_events fe
            USING flow_runs fr
            WHERE fr.id = fe.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_step_attempts fsa
            USING flow_runs fr
            WHERE fr.id = fsa.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_steps fs
            USING flow_runs fr
            WHERE fr.id = fs.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_runs
            WHERE completed_at IS NOT NULL
              AND completed_at < @CutoffUtc;
            """,
            new { CutoffUtc = cutoffUtc },
            tx).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FlowRunRecord?> GetRunCoreAsync(NpgsqlConnection conn, Guid runId)
    {
        return await conn.QuerySingleOrDefaultAsync<FlowRunRecord>(
            """
            SELECT id AS "Id", flow_id AS "FlowId", flow_name AS "FlowName",
                   status AS "Status", trigger_key AS "TriggerKey",
                   trigger_data_json AS "TriggerDataJson", background_job_id AS "BackgroundJobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt"
            FROM flow_runs
            WHERE id = @Id
            """,
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
                stepAttempts = [CreateSyntheticAttempt(step)];

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
            CompletedAt = step.CompletedAt
        };
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
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
