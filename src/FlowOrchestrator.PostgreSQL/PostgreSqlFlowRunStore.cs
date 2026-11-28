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

    public async Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_runs (id, flow_id, flow_name, status, trigger_key, trigger_data_json, background_job_id, started_at, source_run_id)
            VALUES (@Id, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, NOW(), @SourceRunId)
            """,
            new { Id = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId, SourceRunId = sourceRunId });

        return (await GetRunCoreAsync(conn, runId))!;
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetDerivedRunsAsync(Guid sourceRunId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>(
            """
            SELECT id AS Id, flow_id AS FlowId, flow_name AS FlowName, status AS Status,
                   trigger_key AS TriggerKey, trigger_data_json AS TriggerDataJson,
                   background_job_id AS BackgroundJobId, started_at AS StartedAt,
                   completed_at AS CompletedAt, source_run_id AS SourceRunId
            FROM flow_runs
            WHERE source_run_id = @SourceRunId
            ORDER BY started_at DESC
            """,
            new { SourceRunId = sourceRunId });
        return rows.AsList();
    }

    /// <summary>
    /// Records the start of a step attempt, allocating the next attempt number while holding a
    /// transaction-scoped advisory lock keyed on the step so concurrent attempts serialise.
    /// </summary>
    /// <remarks>
    /// SQL Server serialises the <c>MAX(attempt_no)+1</c> allocation with <c>UPDLOCK, HOLDLOCK</c>
    /// — it blocks the second writer rather than aborting it. Postgres has no equivalent row-range
    /// hint for this read-then-insert, and a plain <c>Serializable</c> transaction surfaces the
    /// concurrent collision as an error (either a 40001 serialization failure or, more commonly
    /// here, a 23505 unique-violation on <c>flow_step_attempts_pkey</c> when both transactions
    /// compute the same attempt number). To match SQL Server's non-throwing semantics, this method
    /// takes a <c>pg_advisory_xact_lock</c> keyed on <c>(run_id, step_key)</c> at the top of the
    /// transaction: concurrent callers for the same step block until the holder commits, so the
    /// next <c>MAX(attempt_no)</c> read always sees prior attempts. The lock is released
    /// automatically when the transaction commits or rolls back.
    /// </remarks>
    public Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId)
        => RecordStepStartAsync(runId, stepKey, stepType, inputJson, jobId, serverId: null);

    /// <inheritdoc/>
    public async Task RecordStepStartAsync(Guid runId, string stepKey, string stepType, string? inputJson, string? jobId, string? serverId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Serialise attempt-number allocation for this step across connections (mirrors SQL
        // Server's UPDLOCK/HOLDLOCK). hashtextextended maps the composite key to the bigint the
        // advisory-lock API expects; the lock is transaction-scoped (auto-released on commit/rollback).
        // The same statement takes the run gate shared (see TryTakeOverStaleIdempotencyKeyAsync): no extra
        // round trip, and shared never waits on shared.
        await conn.ExecuteAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(@LockKey, 0)) FROM {RunGateSharedFrom}",
            new { LockKey = $"{runId:N}:{stepKey}", RunId = runId }, tx);

        var attemptNo = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(attempt_no), 0) + 1 FROM flow_step_attempts WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey }, tx);

        await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_attempts (run_id, step_key, attempt_no, step_type, status, input_json, job_id, started_at, server_id)
            VALUES (@RunId, @StepKey, @AttemptNo, @StepType, 'Running', @InputJson, @JobId, NOW(), @ServerId)
            """,
            new { RunId = runId, StepKey = stepKey, AttemptNo = attemptNo, StepType = stepType, InputJson = inputJson, JobId = jobId, ServerId = serverId }, tx);

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

    /// <inheritdoc/>
    public async Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        // One statement, so one transaction. The first UPDATE's predicate is the compare-and-swap: a
        // concurrent caller blocks on the row lock, then re-checks the committed row (no longer Running)
        // and updates nothing; the attempt row is only touched when the step row was.
        var won = await conn.ExecuteScalarAsync<long>(
            """
            WITH won AS (
                UPDATE flow_steps
                SET status = @Status, output_json = @OutputJson, error_message = @ErrorMessage, completed_at = NOW()
                WHERE run_id = @RunId AND step_key = @StepKey AND status = 'Running'
                RETURNING 1
            ),
            latest_attempt AS (
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
                  AND EXISTS (SELECT 1 FROM won)
            )
            SELECT COUNT(*) FROM won
            """,
            new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage });

        return won > 0;
    }

    public async Task CompleteRunAsync(Guid runId, string status)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_runs SET status = @Status, completed_at = NOW() WHERE id = @Id",
            new { Id = runId, Status = status });
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteRunIfActiveAsync(Guid runId, string status)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        // Guarded, atomic transition: only a still-Running run is completed, and rows-affected tells
        // the caller whether THIS call won the race — so the lifecycle event fires exactly once.
        var affected = await conn.ExecuteAsync(
            "UPDATE flow_runs SET status = @Status, completed_at = NOW() WHERE id = @Id AND status = 'Running'",
            new { Id = runId, Status = status });
        return affected > 0;
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetRunsAsync(Guid? flowId = null, int skip = 0, int take = 50)
    {
        // No total is needed here, so this must not go through GetRunsPageAsync: its COUNT(*) OVER()
        // evaluates the filter over every run in retention on each overview poll.
        var page = await SearchRunsAsync(new RunSearchQuery { FlowId = flowId, Skip = skip, Take = take });
        return page.Runs;
    }

    public Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(
        Guid? flowId = null,
        string? status = null,
        int skip = 0,
        int take = 50,
        string? search = null)
        => GetRunsPageAsync(flowId, status, skip, take, search, deepSearch: true);

    public async Task<(IReadOnlyList<FlowRunRecord> Runs, int TotalCount)> GetRunsPageAsync(
        Guid? flowId,
        string? status,
        int skip,
        int take,
        string? search,
        bool deepSearch,
        DateTimeOffset? startedFrom = null,
        DateTimeOffset? startedTo = null)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var whereClauses = new List<string>();
        if (flowId.HasValue)
            whereClauses.Add("fr.flow_id = @FlowId");
        if (!string.IsNullOrWhiteSpace(status))
            whereClauses.Add("fr.status = @Status");
        if (startedFrom.HasValue)
            whereClauses.Add("fr.started_at >= @StartedFrom");
        if (startedTo.HasValue)
            whereClauses.Add("fr.started_at <= @StartedTo");

        var searchLike = (string?)null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchLike = $"%{EscapeLikePattern(search)}%";
            // Always match the cheap top-level run columns. The per-step match (incl.
            // output_json; flow_step_attempts excluded as duplicated history) is only added
            // for deep search. It is a de-correlated IN over BARE columns (no COALESCE): a
            // COALESCE(col,'') wrapper makes the column opaque to the plain-column pg_trgm GIN
            // index (forcing a sequential scan), and a correlated EXISTS is harder for the
            // planner to satisfy from the index. With bare columns the planner uses a BitmapOr
            // across the step_key / error_message / output_json trigram indexes. Dropping
            // COALESCE is behaviour-preserving here: search is non-empty, and both `NULL ILIKE
            // '%x%'` and `'' ILIKE '%x%'` are false.
            var stepMatch = deepSearch
                ? """

                    OR fr.id IN (
                        SELECT fs.run_id
                        FROM flow_steps AS fs
                        WHERE fs.step_key ILIKE @SearchLike
                           OR fs.error_message ILIKE @SearchLike
                           OR fs.output_json ILIKE @SearchLike
                    )
                    """
                : string.Empty;
            whereClauses.Add(
                """
                (
                    fr.id::text ILIKE @SearchLike
                    OR COALESCE(fr.flow_name, '') ILIKE @SearchLike
                    OR COALESCE(fr.trigger_key, '') ILIKE @SearchLike
                    OR COALESCE(fr.status, '') ILIKE @SearchLike
                    OR COALESCE(fr.background_job_id, '') ILIKE @SearchLike
                """ + stepMatch + """

                )
                """);
        }

        var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        // Single pass: COUNT(*) OVER() yields the full filtered total alongside the page,
        // avoiding a second scan of the same predicate.
        var pageSql = $"""
            SELECT fr.id AS "Id", fr.flow_id AS "FlowId", fr.flow_name AS "FlowName",
                   fr.status AS "Status", fr.trigger_key AS "TriggerKey",
                   fr.trigger_data_json AS "TriggerDataJson", fr.background_job_id AS "BackgroundJobId",
                   fr.started_at AS "StartedAt", fr.completed_at AS "CompletedAt",
                   fr.source_run_id AS "SourceRunId", COUNT(*) OVER()::int AS "TotalCount"
            FROM flow_runs AS fr
            """ + whereSql + """
             ORDER BY fr.started_at DESC
            LIMIT @Take OFFSET @Skip
            """;

        // The status filter is case-insensitive on every provider; canonicalising the parameter keeps it an
        // equality on the bare column so ix_flow_runs_flow_id_status_started_at still serves it.
        var parameters = new { FlowId = flowId, Status = status is null ? null : RunStatusNames.Canonicalize(status), Skip = skip, Take = take, SearchLike = searchLike, StartedFrom = startedFrom?.ToUniversalTime(), StartedTo = startedTo?.ToUniversalTime() };
        var totalCount = 0;
        var rows = (await conn.QueryAsync<FlowRunRecord, int, FlowRunRecord>(
            pageSql,
            (run, total) => { totalCount = total; return run; },
            parameters,
            splitOn: "TotalCount")).AsList();

        // COUNT(*) OVER() emits no row — and therefore no total — when the page itself is empty
        // (e.g. skip past the last matching row). Fall back to an explicit COUNT so the reported
        // total stays the full filtered count for page-math, not 0. (::int — PG COUNT is bigint.)
        if (rows.Count == 0 && skip > 0)
            totalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*)::int FROM flow_runs AS fr{whereSql}", parameters);

        return (rows, totalCount);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The batch comes from <see cref="PostgreSqlRunSearchCommandBuilder"/> (shape rules and measurements
    /// there). It needs no optional schema to be correct: without <c>pg_trgm</c> or before the search
    /// index exists, the same text runs as a sequential scan. A hex-shaped term also reads the run-id
    /// prefix as a primary-key range at any age, matching the SQL Server provider.
    /// </remarks>
    public async Task<RunSearchPage> SearchRunsAsync(RunSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);
        var take = ClampTake(query.Take);

        await using var conn = new NpgsqlConnection(_connectionString);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            command.Sql, command.Parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!command.Merge)
        {
            var rows = (await grid.ReadAsync<FlowRunRecord>().ConfigureAwait(false)).AsList();
            int? total = command.CountsTotal ? await grid.ReadSingleAsync<int>().ConfigureAwait(false) : null;
            return RunSearchPage.FromRows(rows, take, total, query.EffectiveTotalCountCap);
        }

        var sets = new List<IReadOnlyList<FlowRunRecord>>(command.RowSets);
        for (var i = 0; i < command.RowSets; i++)
            sets.Add((await grid.ReadAsync<FlowRunRecord>().ConfigureAwait(false)).AsList());
        return RunSearchPage.FromRows(PostgreSqlRunSearchCommandBuilder.Merge(sets, query.Skip, take), take, null, query.EffectiveTotalCountCap);
    }

    /// <summary>Clamps a page size to <c>[0, int.MaxValue - 1]</c> so <c>Take + 1</c> neither goes negative nor overflows.</summary>
    private static int ClampTake(int take) => Math.Clamp(take, 0, int.MaxValue - 1);

    /// <summary>The batch <see cref="SearchRunsAsync"/> sends — what benchmarks and I/O tests measure.</summary>
    /// <param name="query">The structured run query.</param>
    /// <returns>The batch text, its parameters, and whether a count result set follows the rows.</returns>
    internal static (string Sql, object Parameters, bool CountsTotal) BuildSearchRunsCommand(RunSearchQuery query)
    {
        var command = PostgreSqlRunSearchCommandBuilder.Build(query);
        return (command.Sql, command.Parameters, command.CountsTotal);
    }

    /// <inheritdoc/>
    public async Task<FlowRunRecord?> GetRunAsync(Guid runId)
    {
        // One indexed single-row read. The interface default would run GetRunDetailAsync, i.e. this
        // query plus every flow_steps and flow_step_attempts row for the run including their
        // unbounded JSON columns.
        await using var conn = new NpgsqlConnection(_connectionString);
        return await GetRunCoreAsync(conn, runId);
    }

    public async Task<FlowStepRecord?> GetStepAsync(Guid runId, string stepKey)
    {
        // Point read on the (run_id, step_key) key. The interface default would fetch the whole run
        // detail — every step and attempt row with their JSON columns — to return one row.
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowStepRecord>(
            """
            SELECT run_id AS "RunId", step_key AS "StepKey", step_type AS "StepType",
                   status AS "Status", input_json AS "InputJson", output_json AS "OutputJson",
                   error_message AS "ErrorMessage", job_id AS "JobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt",
                   evaluation_trace_json AS "EvaluationTraceJson"
            FROM flow_steps
            WHERE run_id = @RunId AND step_key = @StepKey
            """,
            new { RunId = runId, StepKey = stepKey });
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
                   started_at AS "StartedAt", completed_at AS "CompletedAt",
                   evaluation_trace_json AS "EvaluationTraceJson"
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
                   started_at AS "StartedAt", completed_at AS "CompletedAt",
                   evaluation_trace_json AS "EvaluationTraceJson", server_id AS "ServerId"
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
        // Enabled definitions, as DashboardStatistics.TotalFlows documents. COUNT(DISTINCT flow_id) over
        // flow_runs grew with run history and ran on the dashboard's 5 s poll (#189).
        stats.TotalFlows = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM flow_definitions WHERE is_enabled");
        stats.ActiveRuns = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM flow_runs WHERE status = 'Running'");
        // "Today" is bucketed in UTC so the count matches the other backends regardless of the
        // database session time zone. Boundaries are computed here rather than in SQL deliberately:
        // wrapping completed_at in (… AT TIME ZONE 'UTC')::date made the predicate non-sargable, so
        // both counts sequential-scanned flow_runs — on an endpoint the dashboard polls every
        // 5 seconds, per connected browser. A half-open range over the bare column keeps the
        // identical UTC bucketing and lets the index be used.
        var todayUtc = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var bounds = new { From = new DateTimeOffset(todayUtc, TimeSpan.Zero), To = new DateTimeOffset(todayUtc.AddDays(1), TimeSpan.Zero) };

        stats.CompletedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow_runs WHERE status = 'Succeeded' AND completed_at >= @From AND completed_at < @To", bounds);
        stats.FailedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow_runs WHERE status = 'Failed' AND completed_at >= @From AND completed_at < @To", bounds);
        return stats;
    }

    public async Task<IReadOnlyList<RunTimeseriesBucket>> GetRunTimeseriesAsync(
        RunTimeseriesGranularity granularity,
        DateTimeOffset since,
        DateTimeOffset until,
        Guid? flowId = null)
    {
        // Npgsql binds DateTimeOffset to timestamptz only at offset 0 and throws otherwise; the
        // dashboard forwards whatever offset the caller sent, which surfaced as a 500. Same instant,
        // normalised to UTC, as SearchRunsAsync already does for its window.
        since = since.ToUniversalTime();
        until = until.ToUniversalTime();

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = (await conn.QueryAsync<(DateTimeOffset StartedAt, string Status, double? DurationMs)>(
            $@"SELECT started_at AS ""StartedAt"", status AS ""Status"",
                      CASE WHEN completed_at IS NULL THEN NULL
                           ELSE EXTRACT(EPOCH FROM (completed_at - started_at)) * 1000 END AS ""DurationMs""
                 FROM flow_runs
                WHERE started_at >= @Since AND started_at < @Until
                  {(flowId.HasValue ? "AND flow_id = @FlowId" : string.Empty)}",
            new { Since = since, Until = until, FlowId = flowId })).AsList();

        return TimeseriesAggregator.Aggregate(rows, granularity, since, until);
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>(
            """
            SELECT id AS "Id", flow_id AS "FlowId", flow_name AS "FlowName",
                   status AS "Status", trigger_key AS "TriggerKey",
                   trigger_data_json AS "TriggerDataJson", background_job_id AS "BackgroundJobId",
                   started_at AS "StartedAt", completed_at AS "CompletedAt",
                   source_run_id AS "SourceRunId"
            FROM flow_runs
            WHERE status = 'Running'
            ORDER BY started_at DESC
            """);
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> RetryStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        // One statement, so one transaction. The step UPDATE's predicate is the compare-and-swap: a second
        // concurrent retry blocks on the row lock, re-checks the committed row (no longer Failed) and changes
        // nothing — in particular it no longer deletes the first retry's claim (RFC 2.0 S4). Every other
        // sub-statement is gated on the swap having won. The claim and ledger deletes (v1.22) let the engine
        // dispatch and claim the new attempt.
        var won = await conn.ExecuteScalarAsync<long>(
            """
            WITH won AS (
                UPDATE flow_steps
                SET status = 'Running', output_json = NULL, error_message = NULL,
                    completed_at = NULL, started_at = NOW()
                WHERE run_id = @RunId AND step_key = @StepKey AND status = 'Failed'
                RETURNING 1
            ),
            run_reset AS (
                UPDATE flow_runs SET status = 'Running', completed_at = NULL
                WHERE id = @RunId AND EXISTS (SELECT 1 FROM won)
            ),
            claim_cleared AS (
                DELETE FROM flow_step_claims
                WHERE run_id = @RunId AND step_key = @StepKey AND EXISTS (SELECT 1 FROM won)
            ),
            lease_cleared AS (
                UPDATE flow_step_leases SET expires_at = NULL
                WHERE run_id = @RunId AND step_key = @StepKey AND EXISTS (SELECT 1 FROM won)
            ),
            ledger_cleared AS (
                DELETE FROM flow_step_dispatches
                WHERE run_id = @RunId AND step_key = @StepKey AND EXISTS (SELECT 1 FROM won)
            )
            SELECT COUNT(*) FROM won
            """,
            new { RunId = runId, StepKey = stepKey });

        return won > 0;
    }

    public async Task ResetCascadeSkippedDependentsAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        if (stepKeys is null || stepKeys.Count == 0)
        {
            return;
        }

        var keyArray = stepKeys.ToArray();
        await using var conn = new NpgsqlConnection(_connectionString);
        // flow_step_attempts rows are intentionally NOT deleted — the prior cascade-skip
        // attempt stays as audit history for the dashboard. Only the latest-status row
        // (flow_steps) and the gate rows are cleared so the engine re-evaluates on the
        // next continuation pass.
        //
        // The engine passes EVERY transitive descendant of the retried step, including ones that
        // already Succeeded or were When-skipped. The gate rows must therefore be cleared only for
        // the keys whose flow_steps row was actually the PrerequisitesUnmet skip being reset — the
        // RETURNING of the first data-modifying CTE yields exactly those. Clearing the claim of a
        // Succeeded descendant would let it be claimed and executed a second time, breaking
        // "execute once". One statement, so all three deletes commit or roll back together.
        await conn.ExecuteAsync(
            """
            WITH reset AS (
                DELETE FROM flow_steps
                WHERE run_id = @RunId
                  AND step_key = ANY(@StepKeys)
                  AND status = 'Skipped'
                  AND error_message = @Reason
                RETURNING step_key
            ),
            cleared_claims AS (
                DELETE FROM flow_step_claims fsc
                USING reset r
                WHERE fsc.run_id = @RunId AND fsc.step_key = r.step_key
            ),
            cleared_leases AS (
                UPDATE flow_step_leases fsl SET expires_at = NULL
                FROM reset r
                WHERE fsl.run_id = @RunId AND fsl.step_key = r.step_key
            )
            DELETE FROM flow_step_dispatches fsd
            USING reset r
            WHERE fsd.run_id = @RunId AND fsd.step_key = r.step_key;
            """,
            new { RunId = runId, StepKeys = keyArray, Reason = StepSkipReasons.PrerequisitesUnmet }).ConfigureAwait(false);
    }

    public async Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        // The run gate (shared) is held until this statement commits, so a stale-reservation takeover of the
        // run either sees this row or has latched the run's cancel before it is written. Per dispatch: one
        // shared advisory lock inside the same statement, no extra round trip; shared never waits on shared.
        var rows = await conn.ExecuteAsync(TryRecordDispatchSql, new { RunId = runId, StepKey = stepKey });
        return rows > 0;
    }

    /// <summary>The ledger insert of <see cref="TryRecordDispatchAsync"/>, holding the run gate shared.</summary>
    internal const string TryRecordDispatchSql = $"""
        INSERT INTO flow_step_dispatches (run_id, step_key)
        SELECT @RunId, @StepKey
        FROM {RunGateSharedFrom}
        ON CONFLICT (run_id, step_key) DO NOTHING
        """;

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

    /// <inheritdoc/>
    public async Task<bool> IsStepDispatchedAsync(Guid runId, string stepKey)
    {
        // Point lookup on the (run_id, step_key) primary key. The interface default would fetch every
        // dispatched key in the run — one row per dispatched step — to answer a single-key question,
        // and the ForEach admission gate asks it on every step completion while a loop is parked.
        await using var conn = new NpgsqlConnection(_connectionString);
        var exists = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM flow_step_dispatches WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey });

        return exists is not null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One statement: the CTE picks the qualifying rows with <c>FOR UPDATE OF d SKIP LOCKED</c>, so a
    /// concurrent caller on another replica skips rows this one holds instead of leasing them a second time,
    /// and the <c>UPDATE … RETURNING</c> re-stamps them. The candidate scan is served by the partial index
    /// <c>ix_flow_step_dispatches_unsent</c> (rows with no job id only), so its cost follows the number of
    /// in-flight or stranded dispatches, not the size of the ledger. Ages are measured on the database clock
    /// that stamped <c>dispatched_at</c>.
    /// </remarks>
    public async Task<IReadOnlyList<UnsentDispatch>> LeaseUnsentDispatchesAsync(TimeSpan minimumAge, int maxCount, CancellationToken ct = default)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(Guid RunId, Guid FlowId, string StepKey)>(
            """
            WITH due AS (
                SELECT d.run_id, d.step_key, r.flow_id
                FROM flow_step_dispatches d
                JOIN flow_runs r ON r.id = d.run_id
                WHERE d.dispatch_job_id IS NULL
                  AND d.dispatched_at <= NOW() - make_interval(secs => @AgeSeconds)
                  AND r.status = 'Running'
                  AND NOT EXISTS (
                      SELECT 1 FROM flow_step_claims c
                      WHERE c.run_id = d.run_id AND c.step_key = d.step_key)
                  AND NOT EXISTS (
                      SELECT 1 FROM flow_steps s
                      WHERE s.run_id = d.run_id AND s.step_key = d.step_key AND s.status <> 'Pending')
                ORDER BY d.dispatched_at
                LIMIT @MaxCount
                FOR UPDATE OF d SKIP LOCKED
            )
            UPDATE flow_step_dispatches AS d
            SET dispatched_at = NOW()
            FROM due
            WHERE d.run_id = due.run_id AND d.step_key = due.step_key
            RETURNING d.run_id, due.flow_id, d.step_key
            """,
            new { MaxCount = maxCount, AgeSeconds = Math.Max(0d, minimumAge.TotalSeconds) }).ConfigureAwait(false);

        var leased = new List<UnsentDispatch>();
        foreach (var (runId, flowId, stepKey) in rows)
        {
            leased.Add(new UnsentDispatch(runId, flowId, stepKey));
        }

        return leased;
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

    /// <inheritdoc/>
    /// <remarks>
    /// Primary-key lookups on <c>(run_id, step_key)</c> for the whole key set in one statement
    /// (<c>= ANY(@StepKeys)</c> binds a single array parameter). The statement runs on its own connection
    /// under <c>READ COMMITTED</c>, so its snapshot is taken when it starts and holds every write committed
    /// before — the visibility the engine's frontier read relies on.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        var map = new Dictionary<string, StepStatus>(stepKeys.Count, StringComparer.Ordinal);
        if (stepKeys.Count == 0)
        {
            return map;
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(string StepKey, string Status)>(
            """
            SELECT step_key AS "StepKey", status AS "Status"
            FROM flow_steps
            WHERE run_id = @RunId AND step_key = ANY(@StepKeys)
            """,
            new { RunId = runId, StepKeys = stepKeys.Distinct(StringComparer.Ordinal).ToArray() }).ConfigureAwait(false);
        foreach (var (stepKey, status) in rows)
        {
            map[stepKey] = ParseStepStatus(status);
        }

        return map;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Served by the partial index <c>ix_flow_steps_run_id_open</c>, which holds only Running and Pending rows;
    /// the predicate below implies its <c>WHERE</c>, so the planner can use it. The migrator builds that index
    /// <c>CONCURRENTLY</c> in the background, so the query must not depend on it: until it exists (or while a
    /// build is in flight and the index is still <c>INVALID</c>) the same statement reads the run's rows through
    /// the primary key — correct, only O(steps in the run) instead of O(open steps).
    /// </remarks>
    public async Task<bool> HasRunningOrPendingStepAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM flow_steps
                WHERE run_id = @RunId AND status IN ('Running', 'Pending'))
            """,
            new { RunId = runId }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A primary-key range read of the run's rows filtered to the loop's prefix, grouped by iteration on the
    /// server, so one row travels back. The key shape is checked with an anchored regular expression before
    /// the iteration segment is cast (inside a <c>CASE</c>, because PostgreSQL does not promise to evaluate
    /// the regex first). Only canonical indices count — no leading zero, at most nine digits — which is the
    /// form the engine writes. "Terminal" is every status but Running and Pending, compared case-insensitively:
    /// the verdict <c>ParseStepStatus</c> reaches, which reads an unrecognised status as Failed. Measured at
    /// about 5 ms over 2 000 loop rows and 20 ms over 10 000 (PostgreSQL 18 in Docker) — the text functions
    /// cost roughly four times a plain index scan per row — paid once per iteration settle; the per-loop
    /// counters of RFC 2.0 §4.7 replace it with a single-row read.
    /// </remarks>
    public async Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys)
    {
        if (iterations <= 0 || bodyStepKeys.Count == 0)
        {
            return 0;
        }

        var body = bodyStepKeys.Distinct(StringComparer.Ordinal).ToArray();
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int FROM (
                SELECT split_part(r.rest, '.', 1) AS iteration
                FROM flow_steps AS s
                CROSS JOIN LATERAL (SELECT substr(s.step_key, @PrefixLength + 1) AS rest) AS r
                WHERE s.run_id = @RunId
                  AND left(s.step_key, @PrefixLength) = @Prefix
                  AND lower(s.status) NOT IN ('running', 'pending')
                  AND r.rest ~ '^(0|[1-9][0-9]{0,8})\.[^.]+$'
                  AND (CASE WHEN r.rest ~ '^(0|[1-9][0-9]{0,8})\.[^.]+$'
                            THEN split_part(r.rest, '.', 1)::int END) < @Iterations
                  AND split_part(r.rest, '.', 2) = ANY(@Body)
                GROUP BY 1
                HAVING COUNT(*) = @BodyCount) AS settled
            """,
            new
            {
                RunId = runId,
                Prefix = loopStepKey + ".",
                PrefixLength = loopStepKey.Length + 1,
                Iterations = iterations,
                Body = body,
                BodyCount = body.Length
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<string>(
            "SELECT step_key FROM flow_step_claims WHERE run_id = @RunId",
            new { RunId = runId });

        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> IsStepClaimedAsync(Guid runId, string stepKey)
    {
        // Point lookup on the (run_id, step_key) primary key. The interface default would fetch every
        // claimed key in the run — one row per executed step, since claims are never released on
        // terminal statuses — to answer a single-key question.
        await using var conn = new NpgsqlConnection(_connectionString);
        var exists = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM flow_step_claims WHERE run_id = @RunId AND step_key = @StepKey",
            new { RunId = runId, StepKey = stepKey });

        return exists is not null;
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

    public async Task ReleaseStepClaimAsync(Guid runId, string stepKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            DELETE FROM flow_step_claims WHERE run_id = @RunId AND step_key = @StepKey;
            UPDATE flow_step_leases SET expires_at = NULL WHERE run_id = @RunId AND step_key = @StepKey AND expires_at IS NOT NULL;
            """,
            new { RunId = runId, StepKey = stepKey });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One transaction. A fresh claim (<c>ON CONFLICT DO NOTHING</c>) upserts the lease row so its fence keeps
    /// rising across attempts. A takeover locks the expired lease row (<c>FOR UPDATE</c>) and only then reads the
    /// step's status in a separate statement — its READ COMMITTED snapshot is taken after the lock, so a fenced
    /// completion that committed first (it locks the same row) is always seen. Expiry uses
    /// <c>clock_timestamp()</c>, the database's wall clock.
    /// </remarks>
    public async Task<StepLease?> TryAcquireStepLeaseAsync(Guid runId, string stepKey, string ownerId, TimeSpan duration)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
        var args = new { RunId = runId, StepKey = stepKey, Owner = ownerId, LeaseSeconds = Math.Max(0d, duration.TotalSeconds) };

        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_claims (run_id, step_key, claimed_at)
            VALUES (@RunId, @StepKey, NOW())
            ON CONFLICT (run_id, step_key) DO NOTHING
            """,
            args, tx).ConfigureAwait(false);

        long? fence = null;
        if (inserted > 0)
        {
            fence = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO flow_step_leases (run_id, step_key, fence, owner, expires_at)
                VALUES (@RunId, @StepKey, 1, @Owner, clock_timestamp() + make_interval(secs => @LeaseSeconds))
                ON CONFLICT (run_id, step_key) DO UPDATE
                SET fence = flow_step_leases.fence + 1, owner = EXCLUDED.owner,
                    expires_at = EXCLUDED.expires_at, redelivered_at = NULL
                RETURNING fence
                """,
                args, tx).ConfigureAwait(false);
        }
        else
        {
            var current = await conn.ExecuteScalarAsync<long?>(
                """
                SELECT fence FROM flow_step_leases
                WHERE run_id = @RunId AND step_key = @StepKey
                  AND expires_at IS NOT NULL AND expires_at < clock_timestamp()
                FOR UPDATE
                """,
                args, tx).ConfigureAwait(false);

            if (current is not null)
            {
                var terminal = await conn.ExecuteScalarAsync<int?>(
                    """
                    SELECT 1 FROM flow_steps
                    WHERE run_id = @RunId AND step_key = @StepKey AND status NOT IN ('Running', 'Pending')
                    """,
                    args, tx).ConfigureAwait(false);

                if (terminal is null)
                {
                    fence = await conn.ExecuteScalarAsync<long>(
                        """
                        UPDATE flow_step_leases
                        SET fence = fence + 1, owner = @Owner,
                            expires_at = clock_timestamp() + make_interval(secs => @LeaseSeconds), redelivered_at = NULL
                        WHERE run_id = @RunId AND step_key = @StepKey
                        RETURNING fence
                        """,
                        args, tx).ConfigureAwait(false);
                }
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return fence is { } f ? new StepLease(runId, stepKey, f) : null;
    }

    /// <inheritdoc/>
    /// <remarks>One statement: a fenced <c>UPDATE … FROM unnest(…) RETURNING</c> over every lease.</remarks>
    public async Task<IReadOnlyList<StepLease>> RenewStepLeasesAsync(IReadOnlyList<StepLease> leases, TimeSpan duration)
    {
        if (leases.Count == 0)
        {
            return [];
        }

        var runIds = new Guid[leases.Count];
        var stepKeys = new string[leases.Count];
        var fences = new long[leases.Count];
        for (var i = 0; i < leases.Count; i++)
        {
            runIds[i] = leases[i].RunId;
            stepKeys[i] = leases[i].StepKey;
            fences[i] = leases[i].Fence;
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(Guid RunId, string StepKey, long Fence)>(
            """
            UPDATE flow_step_leases AS l
            SET expires_at = clock_timestamp() + make_interval(secs => @LeaseSeconds)
            FROM unnest(@RunIds, @StepKeys, @Fences) AS j(run_id, step_key, fence)
            WHERE l.run_id = j.run_id AND l.step_key = j.step_key AND l.fence = j.fence AND l.expires_at IS NOT NULL
            RETURNING l.run_id, l.step_key, l.fence
            """,
            new { RunIds = runIds, StepKeys = stepKeys, Fences = fences, LeaseSeconds = Math.Max(0d, duration.TotalSeconds) }).ConfigureAwait(false);

        var renewed = new List<StepLease>(leases.Count);
        foreach (var (runId, stepKey, fence) in rows)
        {
            renewed.Add(new StepLease(runId, stepKey, fence));
        }

        return renewed;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One statement: the claim is deleted only while the lease row still carries this fence; the lease row is
    /// kept (its expiry cleared) so the next attempt's fence is higher.
    /// </remarks>
    public async Task<bool> ReleaseStepLeaseAsync(StepLease lease)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var won = await conn.ExecuteScalarAsync<long>(
            """
            WITH released AS (
                UPDATE flow_step_leases SET expires_at = NULL
                WHERE run_id = @RunId AND step_key = @StepKey AND fence = @Fence
                RETURNING run_id, step_key
            ),
            unclaimed AS (
                DELETE FROM flow_step_claims c
                USING released r
                WHERE c.run_id = r.run_id AND c.step_key = r.step_key
            )
            SELECT COUNT(*) FROM released
            """,
            new { lease.RunId, lease.StepKey, lease.Fence }).ConfigureAwait(false);
        return won > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> PinStepLeaseAsync(StepLease lease)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.ExecuteAsync(
            "UPDATE flow_step_leases SET expires_at = NULL WHERE run_id = @RunId AND step_key = @StepKey AND fence = @Fence",
            new { lease.RunId, lease.StepKey, lease.Fence }).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One transaction: the lease row is locked first (<c>FOR UPDATE</c>; the takeover in
    /// <see cref="TryAcquireStepLeaseAsync"/> locks it too, so the two serialise), the fence and the claim are
    /// checked, then the same writes as <see cref="RecordStepCompleteAsync"/>. A terminal status clears the
    /// lease's expiry, which makes the claim permanent and drops the row from the live-lease index.
    /// </remarks>
    public async Task<bool> TryRecordLeasedStepCompleteAsync(StepLease lease, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
        var args = new
        {
            lease.RunId,
            lease.StepKey,
            lease.Fence,
            Status = status,
            OutputJson = outputJson,
            ErrorMessage = errorMessage,
            Terminal = status is not (nameof(StepStatus.Running) or nameof(StepStatus.Pending)),
        };

        var held = await conn.ExecuteScalarAsync<int?>(
            """
            SELECT 1 FROM flow_step_leases l
            WHERE l.run_id = @RunId AND l.step_key = @StepKey AND l.fence = @Fence
              AND EXISTS (SELECT 1 FROM flow_step_claims c WHERE c.run_id = l.run_id AND c.step_key = l.step_key)
            FOR UPDATE OF l
            """,
            args, tx).ConfigureAwait(false);

        if (held is null)
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            return false;
        }

        await conn.ExecuteAsync(
            """
            UPDATE flow_steps
            SET status = @Status, output_json = @OutputJson, error_message = @ErrorMessage, completed_at = NOW()
            WHERE run_id = @RunId AND step_key = @StepKey;

            UPDATE flow_step_attempts fsa
            SET status = @Status, output_json = @OutputJson, error_message = @ErrorMessage, completed_at = NOW()
            FROM (
                SELECT attempt_no
                FROM flow_step_attempts
                WHERE run_id = @RunId AND step_key = @StepKey
                ORDER BY attempt_no DESC
                LIMIT 1
            ) AS latest
            WHERE fsa.run_id = @RunId AND fsa.step_key = @StepKey AND fsa.attempt_no = latest.attempt_no;

            UPDATE flow_step_leases SET expires_at = NULL
            WHERE @Terminal AND run_id = @RunId AND step_key = @StepKey;
            """,
            args, tx).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One statement: the CTE picks the qualifying rows with <c>FOR UPDATE OF l SKIP LOCKED</c>, so a concurrent
    /// sweep on another replica skips rows this one holds, and the <c>UPDATE … RETURNING</c> stamps them. The
    /// candidate scan is served by the partial index <c>ix_flow_step_leases_live</c>.
    /// </remarks>
    public async Task<IReadOnlyList<ExpiredStepLease>> LeaseExpiredStepLeasesAsync(TimeSpan redeliveryBackoff, int maxCount)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<(Guid RunId, Guid FlowId, string StepKey)>(
            """
            WITH due AS (
                SELECT l.run_id, l.step_key, r.flow_id
                FROM flow_step_leases l
                JOIN flow_runs r ON r.id = l.run_id
                WHERE l.expires_at IS NOT NULL
                  AND l.expires_at < clock_timestamp()
                  AND (l.redelivered_at IS NULL OR l.redelivered_at <= clock_timestamp() - make_interval(secs => @BackoffSeconds))
                  AND r.status = 'Running'
                  AND EXISTS (
                      SELECT 1 FROM flow_step_claims c
                      WHERE c.run_id = l.run_id AND c.step_key = l.step_key)
                  AND NOT EXISTS (
                      SELECT 1 FROM flow_steps s
                      WHERE s.run_id = l.run_id AND s.step_key = l.step_key AND s.status NOT IN ('Running', 'Pending'))
                ORDER BY l.expires_at
                LIMIT @MaxCount
                FOR UPDATE OF l SKIP LOCKED
            )
            UPDATE flow_step_leases AS l
            SET redelivered_at = clock_timestamp()
            FROM due
            WHERE l.run_id = due.run_id AND l.step_key = due.step_key
            RETURNING l.run_id, due.flow_id, l.step_key
            """,
            new { MaxCount = maxCount, BackoffSeconds = Math.Max(0d, redeliveryBackoff.TotalSeconds) }).ConfigureAwait(false);

        var expired = new List<ExpiredStepLease>();
        foreach (var (runId, flowId, stepKey) in rows)
        {
            expired.Add(new ExpiredStepLease(runId, flowId, stepKey));
        }

        return expired;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        // Take each candidate claim's step advisory lock first — the lock RecordStepStartAsync and
        // RecordSkippedStepAsync hold while they write the step's first row — so a first status write
        // racing this transaction either commits before the probe below (the claim is kept) or waits for
        // this transaction to finish. The age is measured on the database clock that stamped claimed_at.
        var candidates = (await conn.QueryAsync<string>(
            """
            SELECT c.step_key
            FROM flow_step_claims c
            WHERE c.run_id = @RunId
              AND c.claimed_at <= NOW() - make_interval(secs => @AgeSeconds)
              AND NOT EXISTS (SELECT 1 FROM flow_steps s WHERE s.run_id = c.run_id AND s.step_key = c.step_key)
            """,
            new { RunId = runId, AgeSeconds = Math.Max(0d, minimumAge.TotalSeconds) }, tx).ConfigureAwait(false)).AsList();

        if (candidates.Count == 0)
        {
            await tx.CommitAsync().ConfigureAwait(false);
            return [];
        }

        var lockKeys = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            lockKeys[i] = $"{runId:N}:{candidates[i]}";
        }

        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(k, 0)) FROM unnest(@LockKeys) AS k ORDER BY k",
            new { LockKeys = lockKeys }, tx).ConfigureAwait(false);

        // Statement-level snapshot taken after the locks: a row committed by a racing writer is visible.
        var released = (await conn.QueryAsync<string>(
            """
            WITH released AS (
                DELETE FROM flow_step_claims c
                WHERE c.run_id = @RunId
                  AND c.step_key = ANY(@Candidates)
                  AND NOT EXISTS (SELECT 1 FROM flow_steps s WHERE s.run_id = c.run_id AND s.step_key = c.step_key)
                RETURNING c.step_key
            ),
            cleared AS (
                DELETE FROM flow_step_dispatches d
                USING released r
                WHERE d.run_id = @RunId AND d.step_key = r.step_key
            ),
            unleased AS (
                UPDATE flow_step_leases l SET expires_at = NULL
                FROM released r
                WHERE l.run_id = @RunId AND l.step_key = r.step_key
            )
            SELECT step_key FROM released
            """,
            new { RunId = runId, Candidates = candidates.ToArray() }, tx).ConfigureAwait(false)).AsList();

        await tx.CommitAsync().ConfigureAwait(false);
        return released;
    }

    public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
        => RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson: null);

    /// <summary>
    /// Records a <see cref="StepStatus.Skipped"/> step and persists the When-clause
    /// evaluation trace onto the current step row and its latest attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Overrides the runtime-store default so PostgreSQL surfaces the "Why skipped"
    /// trace in run detail, matching the SQL Server backend.
    /// </para>
    /// <para>
    /// One transaction (RFC 2.0 P-1(b)): the engine claims the step first and then calls this. Before
    /// 2.0 the skip was a start, a completion and a trace write in up to three transactions, and a crash
    /// between them left a claim with no <c>Skipped</c> row, which the completion gate reads as in-flight
    /// work for ever. The attempt number is allocated under the same advisory lock as
    /// <see cref="RecordStepStartAsync(Guid, string, string, string?, string?, string?)"/>; a <see langword="null"/> trace keeps the row's previous trace.
    /// </para>
    /// </remarks>
    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
    {
        var trace = string.IsNullOrEmpty(evaluationTraceJson) ? null : evaluationTraceJson;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        // Step lock and run gate (shared), as in RecordStepStartAsync.
        await conn.ExecuteAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(@LockKey, 0)) FROM {RunGateSharedFrom}",
            new { LockKey = $"{runId:N}:{stepKey}", RunId = runId }, tx).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO flow_step_attempts (run_id, step_key, attempt_no, step_type, status, error_message, evaluation_trace_json, started_at, completed_at)
            SELECT @RunId, @StepKey, COALESCE(MAX(attempt_no), 0) + 1, @StepType, 'Skipped', @Reason, @Trace, NOW(), NOW()
            FROM flow_step_attempts
            WHERE run_id = @RunId AND step_key = @StepKey;

            INSERT INTO flow_steps (run_id, step_key, step_type, status, input_json, output_json, error_message, job_id, started_at, completed_at, evaluation_trace_json)
            VALUES (@RunId, @StepKey, @StepType, 'Skipped', NULL, NULL, @Reason, NULL, NOW(), NOW(), @Trace)
            ON CONFLICT (run_id, step_key) DO UPDATE SET
                step_type             = EXCLUDED.step_type,
                input_json            = NULL,
                output_json           = NULL,
                error_message         = EXCLUDED.error_message,
                job_id                = NULL,
                status                = 'Skipped',
                started_at            = NOW(),
                completed_at          = NOW(),
                evaluation_trace_json = COALESCE(EXCLUDED.evaluation_trace_json, flow_steps.evaluation_trace_json);
            """,
            new { RunId = runId, StepKey = stepKey, StepType = stepType, Reason = reason, Trace = trace }, tx).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
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
                IdempotencyKey = IdempotencyKeyStorage.ToStorageForm(idempotencyKey),
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

    /// <inheritdoc/>
    /// <remarks>
    /// Does not apply to a run whose cancel was already requested: a user cancel that landed between the
    /// caller's deadline check and this write wins, so the run stays <c>Cancelled</c> and a later retry's
    /// <see cref="ExtendDeadlineAsync"/> cannot clear it (it only un-latches timeout-induced cancels).
    /// </remarks>
    public async Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE flow_run_controls
            SET timed_out_at_utc = NOW(),
                cancel_requested = TRUE,
                cancel_reason = COALESCE(@Reason, cancel_reason, 'Run timed out.'),
                cancel_requested_at_utc = COALESCE(cancel_requested_at_utc, NOW())
            WHERE run_id = @RunId
              AND timed_out_at_utc IS NULL
              AND cancel_requested = FALSE
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        // Every SET expression reads the pre-update row, so the CASEs evaluate the OLD timed_out_at_utc:
        // un-latch the cancel fields only when the termination was timeout-induced, preserving a
        // genuine user cancellation. timed_out_at_utc is cleared unconditionally.
        var affected = await conn.ExecuteAsync(
            """
            UPDATE flow_run_controls
            SET timeout_at_utc = @NewTimeoutAtUtc,
                cancel_requested        = CASE WHEN timed_out_at_utc IS NOT NULL THEN FALSE ELSE cancel_requested END,
                cancel_reason           = CASE WHEN timed_out_at_utc IS NOT NULL THEN NULL  ELSE cancel_reason END,
                cancel_requested_at_utc = CASE WHEN timed_out_at_utc IS NOT NULL THEN NULL  ELSE cancel_requested_at_utc END,
                timed_out_at_utc        = NULL
            WHERE run_id = @RunId
            """,
            new { RunId = runId, NewTimeoutAtUtc = newTimeoutAtUtc });

        return affected > 0;
    }

    public async Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey);

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

    /// <inheritdoc/>
    public async Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            DELETE FROM flow_idempotency_keys
            WHERE flow_id = @FlowId
              AND trigger_key = @TriggerKey
              AND idempotency_key = @IdempotencyKey
              AND run_id = @RunId
            """,
            new
            {
                FlowId = flowId,
                TriggerKey = triggerKey.Trim().ToLowerInvariant(),
                // Same storage form as registration: keys over 256 characters are stored as a digest.
                IdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey),
                RunId = runId
            });

        return affected > 0;
    }

    public async Task<bool> TryRegisterIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey);

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

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// An unlocked probe (the same predicate, read without locks) answers the common case — a duplicate of a
    /// fresh reservation, or of a run that started work however old — with one <c>SELECT</c> and no
    /// transaction or lock; webhook providers re-deliver for days, so late duplicates of live runs are routine.
    /// Only a reservation the probe sees as stale and unstarted enters the transaction, where one statement
    /// re-checks the whole predicate
    /// (owner, age on <c>NOW()</c> — the clock that stamped <c>created_at_utc</c> — and no
    /// <c>flow_step_dispatches</c> / <c>flow_steps</c> row for the stale run) and, in a data-modifying CTE,
    /// sets the stale run's cancel latch.
    /// </para>
    /// <para>
    /// Locks: the key row's lock serialises concurrent takeovers (the second re-evaluates the updated row,
    /// sees the new owner and matches nothing). PostgreSQL has no key-range locks under READ COMMITTED, so a
    /// slow original trigger could otherwise insert its first ledger or step row after the statement's
    /// snapshot and run that step before the latch commits. The transaction therefore first takes the stale
    /// run's <b>run gate</b> (<see cref="RunGateExclusiveSql"/>) exclusively. Every writer of a
    /// <c>flow_step_dispatches</c> or <c>flow_steps</c> row holds the same gate <i>shared</i> until it commits
    /// (<see cref="TryRecordDispatchAsync"/>, <see cref="RecordStepStartAsync(Guid, string, string, string?, string?, string?)"/>,
    /// <see cref="RecordSkippedStepAsync(Guid, string, string, string?, string?)"/>). A writer already holding
    /// it makes the takeover wait until its row is committed, and the check-and-swap then runs as a separate
    /// statement whose snapshot sees it. A writer arriving later waits until the latch is committed. The gate
    /// is per run: writers of other runs, and writers of one run among themselves (shared with shared),
    /// never wait on it.
    /// </para>
    /// </remarks>
    public async Task<Guid?> TryTakeOverStaleIdempotencyKeyAsync(
        Guid flowId,
        string triggerKey,
        string idempotencyKey,
        Guid staleRunId,
        Guid newRunId,
        TimeSpan reservationTimeout)
    {
        var args = new
        {
            FlowId = flowId,
            TriggerKey = triggerKey.Trim().ToLowerInvariant(),
            IdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey),
            StaleRunId = staleRunId,
            NewRunId = newRunId,
            Timeout = reservationTimeout,
            Reason = IdempotencyKeyStorage.TakenOverCancelReason(newRunId),
            // The control record keeps the caller's spelling, like ConfigureRunAsync.
            ControlTriggerKey = triggerKey,
            ControlIdempotencyKey = IdempotencyKeyStorage.ToStorageForm(idempotencyKey)
        };

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        var probe = await conn.QuerySingleOrDefaultAsync<IdempotencyOwnerProbe>(
            """
            SELECT run_id AS "RunId",
                   (run_id = @StaleRunId
                    AND NOW() - created_at_utc >= @Timeout
                    AND NOT EXISTS (SELECT 1 FROM flow_step_dispatches WHERE run_id = @StaleRunId)
                    AND NOT EXISTS (SELECT 1 FROM flow_steps WHERE run_id = @StaleRunId)) AS "Candidate"
            FROM flow_idempotency_keys
            WHERE flow_id = @FlowId
              AND trigger_key = @TriggerKey
              AND idempotency_key = @IdempotencyKey
            """,
            args).ConfigureAwait(false);

        if (probe is not { Candidate: true })
        {
            return probe?.RunId;
        }

        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

        // Its own statement, so the check-and-swap below takes its READ COMMITTED snapshot after the wait and
        // sees every first row committed by a writer that held the gate.
        await conn.ExecuteAsync(RunGateExclusiveSql, new { RunId = staleRunId }, tx).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            WITH taken AS (
                UPDATE flow_idempotency_keys
                SET run_id = @NewRunId,
                    created_at_utc = NOW()
                WHERE flow_id = @FlowId
                  AND trigger_key = @TriggerKey
                  AND idempotency_key = @IdempotencyKey
                  AND run_id = @StaleRunId
                  AND NOW() - created_at_utc >= @Timeout
                  AND NOT EXISTS (SELECT 1 FROM flow_step_dispatches WHERE run_id = @StaleRunId)
                  AND NOT EXISTS (SELECT 1 FROM flow_steps WHERE run_id = @StaleRunId)
                RETURNING run_id
            )
            INSERT INTO flow_run_controls
                (run_id, flow_id, trigger_key, idempotency_key, timeout_at_utc,
                 cancel_requested, cancel_reason, cancel_requested_at_utc)
            SELECT @StaleRunId, @FlowId, @ControlTriggerKey, @ControlIdempotencyKey, NULL, TRUE, @Reason, NOW()
            FROM taken
            ON CONFLICT (run_id) DO UPDATE SET
                cancel_requested = TRUE,
                cancel_reason = CASE WHEN flow_run_controls.cancel_requested
                                     THEN flow_run_controls.cancel_reason
                                     ELSE EXCLUDED.cancel_reason END,
                cancel_requested_at_utc = COALESCE(flow_run_controls.cancel_requested_at_utc, EXCLUDED.cancel_requested_at_utc);
            """,
            args,
            tx).ConfigureAwait(false);

        // A new snapshot (READ COMMITTED): ours when the UPDATE matched, otherwise whoever committed a
        // takeover first (it held the run gate, and our UPDATE then re-read the row it had updated).
        var owner = await conn.ExecuteScalarAsync<Guid?>(FindOwnerSql, args, tx).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return owner;
    }

    /// <summary>
    /// Advisory-lock class of the per-run gate between a stale-reservation takeover and the writers of a run's
    /// ledger and step rows (ASCII <c>"FOIR"</c>).
    /// </summary>
    /// <remarks>
    /// The two-key form <c>(class, hashtext(run_id::text))</c> lives in a different advisory-lock key space from
    /// the single-<c>bigint</c> per-step locks, so the two can never collide. Two runs whose ids hash alike only
    /// make a takeover wait for an unrelated writer; no decision changes. A string so the SQL below stays a
    /// compile-time constant (no per-dispatch allocation); <c>0x464F4952</c> in decimal.
    /// </remarks>
    internal const string RunGateLockClass = "1179601234";

    /// <summary>Takes the run gate of <c>@RunId</c> exclusively until the transaction ends (takeover side).</summary>
    internal const string RunGateExclusiveSql = $"SELECT pg_advisory_xact_lock({RunGateLockClass}, hashtext(@RunId::text))";

    /// <summary>
    /// <c>FROM</c>-clause item taking the run gate of <c>@RunId</c> shared until the statement's transaction
    /// ends (writer side); evaluated before the row it guards is written.
    /// </summary>
    internal const string RunGateSharedFrom = $"(SELECT pg_advisory_xact_lock_shared({RunGateLockClass}, hashtext(@RunId::text))) AS run_gate";

    private const string FindOwnerSql = """
        SELECT run_id
        FROM flow_idempotency_keys
        WHERE flow_id = @FlowId
          AND trigger_key = @TriggerKey
          AND idempotency_key = @IdempotencyKey
        """;

    /// <summary>Row of the unlocked probe in <see cref="TryTakeOverStaleIdempotencyKeyAsync"/>.</summary>
    /// <param name="RunId">The run the key maps to.</param>
    /// <param name="Candidate">Whether the row is owned by the stale run and old enough to take over.</param>
    private sealed record IdempotencyOwnerProbe(Guid RunId, bool Candidate);

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

            DELETE FROM flow_step_leases fsl
            USING flow_runs fr
            WHERE fr.id = fsl.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            -- flow_step_dispatches and flow_signal_waiters are keyed (run_id, step_key) with no
            -- foreign key to flow_runs, so deleting the run does NOT cascade to them. Without these
            -- two statements both tables accumulate one row per dispatched step / parked signal for
            -- every run ever executed and are never reclaimed by retention.
            DELETE FROM flow_step_dispatches fsd
            USING flow_runs fr
            WHERE fr.id = fsd.run_id
              AND fr.completed_at IS NOT NULL
              AND fr.completed_at < @CutoffUtc;

            DELETE FROM flow_signal_waiters fsw
            USING flow_runs fr
            WHERE fr.id = fsw.run_id
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
                   started_at AS "StartedAt", completed_at AS "CompletedAt",
                   source_run_id AS "SourceRunId"
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
            CompletedAt = step.CompletedAt,
            // Carry the When-clause trace too, so a skipped step without attempt history still shows
            // "Why skipped" in run detail — matching the SQL Server backend.
            EvaluationTraceJson = step.EvaluationTraceJson
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
