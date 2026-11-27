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
/// Step claim deduplication uses a <c>MERGE WITH (HOLDLOCK, UPDLOCK)</c> insert-if-absent, so concurrent
/// claimers of one step get exactly one <see langword="true"/> and never a duplicate-key error.
/// </summary>
public sealed class SqlFlowRunStore :
    IFlowRunStore,
    IFlowRunRuntimeStore,
    IFlowRunControlStore,
    IFlowRetentionStore
{
    private readonly string _connectionString;

    /// <summary>The built-in token index, when <c>UseRunSearchIndex()</c> is on; its writes are folded into this store's batches.</summary>
    private readonly SqlServerRunSearchTokenIndex? _tokenIndex;

    /// <summary>A host-registered <see cref="IRunSearchIndex"/> (not the built-in one), fed after durable writes and asked for deep searches.</summary>
    private readonly IRunSearchIndex? _externalIndex;

    private readonly SqlRunSearchSchemaCache _searchSchema;

    /// <summary>Creates the store with the default run search (no token index).</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    public SqlFlowRunStore(string connectionString)
        : this(connectionString, searchIndex: null)
    {
    }

    /// <summary>Creates the store with an optional run-search index.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    /// <param name="searchIndex">
    /// <see cref="SqlServerRunSearchTokenIndex"/> to maintain and query the built-in token table, any other
    /// <see cref="IRunSearchIndex"/> to feed and query an external index, or <see langword="null"/>.
    /// </param>
    public SqlFlowRunStore(string connectionString, IRunSearchIndex? searchIndex)
    {
        _connectionString = connectionString;
        _tokenIndex = searchIndex as SqlServerRunSearchTokenIndex;
        _externalIndex = _tokenIndex is null ? searchIndex : null;
        _searchSchema = new SqlRunSearchSchemaCache(connectionString, tokensEnabled: _tokenIndex is not null);
    }

    public async Task<FlowRunRecord> StartRunAsync(Guid flowId, string flowName, Guid runId, string triggerKey, string? triggerData, string? jobId, Guid? sourceRunId = null)
    {
        await using var conn = new SqlConnection(_connectionString);
        var tokens = _tokenIndex?.TokenizeRun(triggerData);
        if (tokens is null)
        {
            await conn.ExecuteAsync("""
                INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, SourceRunId)
                VALUES (@Id, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, SYSDATETIMEOFFSET(), @SourceRunId)
                """, new { Id = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId, SourceRunId = sourceRunId });
        }
        else
        {
            // Same round trip; the token insert is its own best-effort statement after the run row.
            await conn.ExecuteAsync("""
                DECLARE @SearchStartedAt DATETIMEOFFSET = SYSDATETIMEOFFSET();
                INSERT INTO FlowRuns (Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, SourceRunId)
                VALUES (@SearchRunId, @FlowId, @FlowName, 'Running', @TriggerKey, @TriggerDataJson, @BackgroundJobId, @SearchStartedAt, @SourceRunId);
                """ + SqlServerRunSearchTokenIndex.InsertTokensSql,
                new { SearchRunId = runId, FlowId = flowId, FlowName = flowName, TriggerKey = triggerKey, TriggerDataJson = triggerData, BackgroundJobId = jobId, SourceRunId = sourceRunId, SearchTokens = tokens });
        }

        var run = (await GetRunCoreAsync(conn, runId))!;
        if (_externalIndex is { } index)
        {
            var document = new RunSearchDocument(run.Id, run.StartedAt, run.FlowName, run.TriggerKey, run.BackgroundJobId, run.TriggerDataJson);
            await IndexBestEffortAsync(() => index.IndexRunAsync(document, CancellationToken.None)).ConfigureAwait(false);
        }

        return run;
    }

    /// <summary>Awaits a write to an external search index and swallows its failure: an index must never fail the durable write it follows.</summary>
    private static async ValueTask IndexBestEffortAsync(Func<ValueTask> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort by contract (IRunSearchIndex remarks): the index may lag, the run state may not.
        }
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

            MERGE FlowSteps WITH (HOLDLOCK, UPDLOCK) AS target
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

        // Token index (opt-in): O(size of this step's key, error and output) to tokenize, plus one
        // statement in the same round trip AFTER the commit — never inside the step's transaction, so a
        // duplicate-key wait between concurrent steps of one run cannot hold the step's row locks. Off:
        // one null check.
        var tokens = _tokenIndex?.TokenizeStep(stepKey, errorMessage, outputJson);
        if (tokens is not null)
        {
            await conn.ExecuteAsync(RecordStepCompleteSql + """

                DECLARE @SearchStartedAt DATETIMEOFFSET = (SELECT StartedAt FROM FlowRuns WHERE Id = @RunId);
                DECLARE @SearchRunId UNIQUEIDENTIFIER = @RunId;
                """ + SqlServerRunSearchTokenIndex.InsertTokensSql,
                new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage, SearchTokens = tokens });
        }
        else
        {
            await conn.ExecuteAsync(RecordStepCompleteSql,
                new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage });
        }

        if (_externalIndex is { } index)
        {
            var document = new StepSearchDocument(runId, stepKey, status, errorMessage, outputJson);
            await IndexBestEffortAsync(() => index.IndexStepAsync(document, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private const string RecordStepCompleteSql = """
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
            """;

    /// <inheritdoc/>
    public async Task<bool> TryCompleteRunningStepAsync(Guid runId, string stepKey, string status, string? outputJson, string? errorMessage)
    {
        await using var conn = new SqlConnection(_connectionString);

        // One round trip, one transaction. The UPDATE's predicate is the compare-and-swap: a second
        // caller blocks on the first one's row lock and then re-reads a row that is no longer Running.
        // The token write, when on, runs after the commit and only for the winner.
        var tokens = _tokenIndex?.TokenizeStep(stepKey, errorMessage, outputJson);
        var won = tokens is not null
            ? await conn.ExecuteScalarAsync<int>(TryCompleteRunningStepSql + """

                DECLARE @SearchStartedAt DATETIMEOFFSET = CASE WHEN @Won > 0 THEN (SELECT StartedAt FROM FlowRuns WHERE Id = @RunId) END;
                DECLARE @SearchRunId UNIQUEIDENTIFIER = @RunId;
                """ + SqlServerRunSearchTokenIndex.InsertTokensSql + """

                SELECT @Won;
                """,
                new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage, SearchTokens = tokens })
            : await conn.ExecuteScalarAsync<int>(TryCompleteRunningStepSql + "SELECT @Won;",
                new { RunId = runId, StepKey = stepKey, Status = status, OutputJson = outputJson, ErrorMessage = errorMessage });

        if (won > 0 && _externalIndex is { } index)
        {
            var document = new StepSearchDocument(runId, stepKey, status, errorMessage, outputJson);
            await IndexBestEffortAsync(() => index.IndexStepAsync(document, CancellationToken.None)).ConfigureAwait(false);
        }

        return won > 0;
    }

    private const string TryCompleteRunningStepSql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            UPDATE FlowSteps
            SET Status = @Status, OutputJson = @OutputJson, ErrorMessage = @ErrorMessage, CompletedAt = SYSDATETIMEOFFSET()
            WHERE RunId = @RunId AND StepKey = @StepKey AND Status = 'Running';

            DECLARE @Won INT = @@ROWCOUNT;

            IF @Won > 0
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
                   AND latest.AttemptNo = attempts.AttemptNo;

            COMMIT TRANSACTION;

            """;

    public async Task CompleteRunAsync(Guid runId, string status)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowRuns SET Status = @Status, CompletedAt = SYSDATETIMEOFFSET() WHERE Id = @Id",
            new { Id = runId, Status = status });
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteRunIfActiveAsync(Guid runId, string status)
    {
        await using var conn = new SqlConnection(_connectionString);
        // Guarded, atomic transition: only a still-Running run is completed, and rows-affected tells
        // the caller whether THIS call won the race — so the lifecycle event fires exactly once.
        var affected = await conn.ExecuteAsync(
            "UPDATE FlowRuns SET Status = @Status, CompletedAt = SYSDATETIMEOFFSET() WHERE Id = @Id AND Status = 'Running'",
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
        await using var conn = new SqlConnection(_connectionString);
        var whereClauses = new List<string>();
        if (flowId.HasValue)
            whereClauses.Add("fr.FlowId = @FlowId");
        if (!string.IsNullOrWhiteSpace(status))
            whereClauses.Add("fr.Status = @Status");
        if (startedFrom.HasValue)
            whereClauses.Add("fr.StartedAt >= @StartedFrom");
        if (startedTo.HasValue)
            whereClauses.Add("fr.StartedAt <= @StartedTo");
        var searchLike = (string?)null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchLike = $"%{EscapeLikePattern(search.ToLowerInvariant())}%";
            // Always match the cheap top-level run columns. The per-step EXISTS scan
            // (incl. OutputJson; FlowStepAttempts is excluded as duplicated history) is only
            // added for deep search — quick search stays index-friendlier for typeahead.
            var stepExists = deepSearch
                ? """

                    OR EXISTS (
                        SELECT 1
                        FROM FlowSteps AS fs
                        WHERE fs.RunId = fr.Id
                          AND (
                                LOWER(fs.StepKey) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                                OR LOWER(fs.ErrorMessage) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                                OR LOWER(fs.OutputJson) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                              )
                    )
                    """
                : string.Empty;
            whereClauses.Add("""
                (
                    LOWER(CONVERT(NVARCHAR(36), fr.Id)) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                    OR LOWER(fr.FlowName) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                    OR LOWER(fr.TriggerKey) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                    OR LOWER(fr.Status) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                    OR LOWER(fr.BackgroundJobId) COLLATE Latin1_General_100_BIN2 LIKE @SearchLike ESCAPE '\'
                """ + stepExists + """

                )
                """);
        }

        var whereSql = whereClauses.Count > 0 ? $" WHERE {string.Join(" AND ", whereClauses)}" : string.Empty;
        // Single pass: COUNT(*) OVER() returns the full filtered total alongside each page row,
        // avoiding a second scan of the same predicate.
        var pageSql = "SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId, fr.StartedAt, fr.CompletedAt, fr.SourceRunId, COUNT(*) OVER() AS TotalCount FROM FlowRuns AS fr"
            + whereSql
            + " ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

        var parameters = new { FlowId = flowId, Status = status, Skip = skip, Take = take, SearchLike = searchLike, StartedFrom = startedFrom, StartedTo = startedTo };
        var totalCount = 0;
        var rows = (await conn.QueryAsync<FlowRunRecord, int, FlowRunRecord>(
            pageSql,
            (run, total) => { totalCount = total; return run; },
            parameters,
            splitOn: "TotalCount")).AsList();

        // COUNT(*) OVER() emits no row — and therefore no total — when the page itself is empty
        // (e.g. skip past the last matching row). Fall back to an explicit COUNT so the reported
        // total stays the full filtered count for page-math, not 0.
        if (rows.Count == 0 && skip > 0)
            totalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM FlowRuns AS fr{whereSql}", parameters);

        return (rows, totalCount);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The batch comes from <see cref="SqlRunSearchCommandBuilder"/> (shape rules and measurements there),
    /// built against a cached snapshot of which search structures exist, so a database whose indexes are
    /// still being built (or were never built) gets fallback shapes instead of an error. A hex-shaped term
    /// also seeks the run-id prefix at any age; deep search uses the token index when it is enabled.
    /// </remarks>
    public async Task<RunSearchPage> SearchRunsAsync(RunSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var schema = await _searchSchema.GetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SearchRunsCoreAsync(query, schema, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsMissingSearchStructure(ex))
        {
            // A structure the snapshot promised is gone (dropped by hand, restored backup): forget the
            // snapshot and run the shape that needs none of them.
            _searchSchema.Invalidate();
            return await SearchRunsCoreAsync(query, SqlRunSearchSchema.None, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Invalid column (207), invalid object (208), missing index in a hint (308).</summary>
    private static bool IsMissingSearchStructure(SqlException ex) => ex.Number is 207 or 208 or 308;

    private async Task<RunSearchPage> SearchRunsCoreAsync(RunSearchQuery query, SqlRunSearchSchema schema, CancellationToken cancellationToken)
    {
        var command = SqlRunSearchCommandBuilder.Build(query, schema, hasExternalIndex: _externalIndex is not null);
        var take = SqlRunSearchCommandBuilder.ClampTake(query.Take);

        await using var conn = new SqlConnection(_connectionString);
        List<FlowRunRecord> rows;
        int? total = null;
        using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            command.Sql, command.Parameters, cancellationToken: cancellationToken)).ConfigureAwait(false))
        {
            if (!command.Merge)
            {
                rows = (await grid.ReadAsync<FlowRunRecord>().ConfigureAwait(false)).AsList();
                if (command.CountsTotal)
                    total = await grid.ReadSingleAsync<int>().ConfigureAwait(false);
                return RunSearchPage.FromRows(rows, take, total, query.EffectiveTotalCountCap);
            }

            var sets = new List<IReadOnlyList<FlowRunRecord>>(command.RowSets + 1);
            for (var i = 0; i < command.RowSets; i++)
                sets.Add((await grid.ReadAsync<FlowRunRecord>().ConfigureAwait(false)).AsList());

            if (command.ExternalIndexText is { } text && _externalIndex is { } index)
                sets.Add(await SearchExternalIndexAsync(conn, index, query, text, cancellationToken).ConfigureAwait(false));

            rows = SqlRunSearchCommandBuilder.Merge(sets, query.Skip, take);
        }

        return RunSearchPage.FromRows(rows, take, total, query.EffectiveTotalCountCap);
    }

    /// <summary>Asks the external index for run ids, then reads those runs by primary key under the query's filters.</summary>
    private static async Task<IReadOnlyList<FlowRunRecord>> SearchExternalIndexAsync(
        SqlConnection conn, IRunSearchIndex index, RunSearchQuery query, string text, CancellationToken cancellationToken)
    {
        var limit = (int)Math.Min((long)Math.Max(0, query.Skip) + SqlRunSearchCommandBuilder.ClampTake(query.Take) + 1, MaxExternalHits);
        var result = await index.SearchAsync(
            new RunSearchIndexQuery(text, IncludeStepPayloads: true, query.StartedFrom, query.StartedTo, limit), cancellationToken).ConfigureAwait(false);
        if (result.Hits.Count == 0)
            return [];

        var ids = new Guid[result.Hits.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = result.Hits[i].RunId;
        var (sql, parameters) = SqlRunSearchCommandBuilder.BuildHydrate(query, ids);
        return (await conn.QueryAsync<FlowRunRecord>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    /// <summary>Most ids hydrated from an external index per search; one parameter each, well under SQL Server's 2,100.</summary>
    private const int MaxExternalHits = 1_000;

    /// <summary>
    /// The batch <see cref="SearchRunsAsync"/> sends when every quick-search structure exists — the shape
    /// benchmarks and I/O tests measure.
    /// </summary>
    /// <param name="query">The structured run query.</param>
    /// <returns>The batch text, its parameters, and whether a count result set follows the rows.</returns>
    internal static (string Sql, object Parameters, bool CountsTotal) BuildSearchRunsCommand(RunSearchQuery query)
        => BuildSearchRunsCommand(query, tokenIndex: false);

    /// <summary>As <see cref="BuildSearchRunsCommand(RunSearchQuery)"/>, optionally with a fully back-filled token index.</summary>
    /// <param name="query">The structured run query.</param>
    /// <param name="tokenIndex">Build the deep-search shape that probes <c>FlowRunSearchTokens</c>.</param>
    /// <returns>The batch text, its parameters, and whether a count result set follows the rows.</returns>
    internal static (string Sql, object Parameters, bool CountsTotal) BuildSearchRunsCommand(RunSearchQuery query, bool tokenIndex)
    {
        var schema = tokenIndex ? SqlRunSearchSchema.Complete with { TokenTable = true } : SqlRunSearchSchema.Complete;
        var command = SqlRunSearchCommandBuilder.Build(query, schema);
        return (command.Sql, command.Parameters, command.CountsTotal);
    }

    /// <inheritdoc/>
    public async Task<FlowRunRecord?> GetRunAsync(Guid runId)
    {
        // One indexed single-row read. The interface default would run GetRunDetailAsync, i.e. this
        // query plus every FlowSteps and FlowStepAttempts row for the run including their
        // NVARCHAR(MAX) JSON columns.
        await using var conn = new SqlConnection(_connectionString);
        return await GetRunCoreAsync(conn, runId);
    }

    public async Task<FlowStepRecord?> GetStepAsync(Guid runId, string stepKey)
    {
        // Point read on the (RunId, StepKey) key. The interface default would fetch the whole run
        // detail — every step and attempt row with their NVARCHAR(MAX) columns — to return one row.
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowStepRecord>(
            "SELECT RunId, StepKey, StepType, Status, InputJson, OutputJson, ErrorMessage, JobId, StartedAt, CompletedAt, EvaluationTraceJson FROM FlowSteps WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey });
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
        // Enabled definitions, as DashboardStatistics.TotalFlows documents. COUNT(DISTINCT FlowId) over
        // FlowRuns grew with run history and ran on the dashboard's 5 s poll (#189).
        stats.TotalFlows = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FlowDefinitions WHERE IsEnabled = 1");
        stats.ActiveRuns = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Running'");
        // "Today" is bucketed in UTC so the count is identical across storage backends and
        // independent of the SQL Server host's local time zone. The boundaries are computed here
        // rather than in SQL deliberately: wrapping CompletedAt in CAST(... AT TIME ZONE ... AS DATE)
        // made the predicate non-sargable, so both counts scanned FlowRuns — on an endpoint the
        // dashboard polls every 5 seconds, per connected browser. A half-open range over the bare
        // column keeps the identical UTC bucketing and lets the index seek.
        var todayUtc = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var bounds = new { From = new DateTimeOffset(todayUtc, TimeSpan.Zero), To = new DateTimeOffset(todayUtc.AddDays(1), TimeSpan.Zero) };

        stats.CompletedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Succeeded' AND CompletedAt >= @From AND CompletedAt < @To", bounds);
        stats.FailedToday = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM FlowRuns WHERE Status = 'Failed' AND CompletedAt >= @From AND CompletedAt < @To", bounds);
        return stats;
    }

    public async Task<IReadOnlyList<RunTimeseriesBucket>> GetRunTimeseriesAsync(
        RunTimeseriesGranularity granularity,
        DateTimeOffset since,
        DateTimeOffset until,
        Guid? flowId = null)
    {
        // Pull only the columns needed for bucket assignment + percentile calc and aggregate
        // in-process. Doing the percentile in SQL Server (PERCENTILE_CONT as a window) is much
        // slower than streaming a few thousand (StartedAt, Status, durationMs) rows back.
        await using var conn = new SqlConnection(_connectionString);
        var rows = (await conn.QueryAsync<(DateTimeOffset StartedAt, string Status, double? DurationMs)>(
            $@"SELECT StartedAt, Status,
                      CASE WHEN CompletedAt IS NULL THEN NULL
                           ELSE DATEDIFF_BIG(MILLISECOND, StartedAt, CompletedAt) END AS DurationMs
                 FROM FlowRuns
                WHERE StartedAt >= @Since AND StartedAt < @Until
                  {(flowId.HasValue ? "AND FlowId = @FlowId" : string.Empty)}",
            new { Since = since, Until = until, FlowId = flowId })).AsList();

        return TimeseriesAggregator.Aggregate(rows, granularity, since, until);
    }

    public async Task<IReadOnlyList<FlowRunRecord>> GetActiveRunsAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowRunRecord>(
            "SELECT Id, FlowId, FlowName, Status, TriggerKey, TriggerDataJson, BackgroundJobId, StartedAt, CompletedAt, SourceRunId FROM FlowRuns WHERE Status = 'Running' ORDER BY StartedAt DESC");
        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> RetryStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new SqlConnection(_connectionString);

        // One transaction. The first UPDATE's predicate is the compare-and-swap: a second concurrent retry
        // blocks on the step row's lock, then re-reads a row that is no longer Failed and changes nothing —
        // in particular it no longer deletes the first retry's claim (RFC 2.0 S4). The claim and ledger
        // deletes (v1.22) let the engine dispatch and claim the new attempt.
        var won = await conn.ExecuteScalarAsync<int>("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            UPDATE FlowSteps
            SET Status = 'Running', OutputJson = NULL, ErrorMessage = NULL, CompletedAt = NULL, StartedAt = SYSDATETIMEOFFSET()
            WHERE RunId = @RunId AND StepKey = @StepKey AND Status = 'Failed';

            DECLARE @Won INT = @@ROWCOUNT;

            IF @Won > 0
            BEGIN
                UPDATE FlowRuns SET Status = 'Running', CompletedAt = NULL WHERE Id = @RunId;
                DELETE FROM FlowStepClaims WHERE RunId = @RunId AND StepKey = @StepKey;
                DELETE FROM FlowStepDispatches WHERE RunId = @RunId AND StepKey = @StepKey;
            END

            COMMIT TRANSACTION;

            SELECT @Won;
            """, new { RunId = runId, StepKey = stepKey });

        return won > 0;
    }

    public async Task ResetCascadeSkippedDependentsAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        if (stepKeys is null || stepKeys.Count == 0)
        {
            return;
        }

        await using var conn = new SqlConnection(_connectionString);
        // FlowStepAttempts rows are intentionally NOT deleted — the prior cascade-skip
        // attempt stays as audit history for the dashboard. Only the latest-status row
        // (FlowSteps) and the gate rows (Claims / Dispatches) are cleared so the engine
        // re-evaluates the step on the next continuation pass.
        //
        // The engine passes EVERY transitive descendant of the retried step, including ones that
        // already Succeeded or were When-skipped. The gate rows must therefore be cleared only for
        // the keys whose FlowSteps row was actually the PrerequisitesUnmet skip being reset — the
        // OUTPUT clause captures exactly those. Clearing the claim of a Succeeded descendant would
        // let it be claimed and executed a second time, breaking "execute once".
        await conn.ExecuteAsync(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @Reset TABLE (StepKey NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);

            DELETE FROM FlowSteps
            OUTPUT deleted.StepKey INTO @Reset (StepKey)
            WHERE RunId = @RunId
              AND StepKey IN @StepKeys
              AND Status = 'Skipped'
              AND ErrorMessage = @Reason;

            DELETE fsc
            FROM FlowStepClaims AS fsc
            INNER JOIN @Reset AS r ON r.StepKey = fsc.StepKey
            WHERE fsc.RunId = @RunId;

            DELETE fsd
            FROM FlowStepDispatches AS fsd
            INNER JOIN @Reset AS r ON r.StepKey = fsd.StepKey
            WHERE fsd.RunId = @RunId;

            COMMIT TRANSACTION;
            """,
            new { RunId = runId, StepKeys = stepKeys, Reason = StepSkipReasons.PrerequisitesUnmet }).ConfigureAwait(false);
    }

    public async Task<bool> TryRecordDispatchAsync(Guid runId, string stepKey, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        // UPDLOCK, HOLDLOCK on the probe takes a key-range lock in update mode, so a concurrent
        // recorder of the same key blocks until this insert commits and then sees the row, instead of
        // both passing NOT EXISTS and the loser hitting a PK violation. The catch covers anything
        // that still races through: losing the insert means "already recorded", not an error.
        try
        {
            var rows = await conn.ExecuteAsync("""
                INSERT INTO FlowStepDispatches (RunId, StepKey)
                SELECT @RunId, @StepKey
                WHERE NOT EXISTS (
                    SELECT 1 FROM FlowStepDispatches WITH (UPDLOCK, HOLDLOCK)
                    WHERE RunId = @RunId AND StepKey = @StepKey)
                """, new { RunId = runId, StepKey = stepKey });
            return rows > 0;
        }
        catch (SqlException ex) when (SqlUniqueViolation.Matches(ex))
        {
            return false;
        }
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

    /// <inheritdoc/>
    public async Task<bool> IsStepDispatchedAsync(Guid runId, string stepKey)
    {
        // Point lookup on the (RunId, StepKey) primary key. The interface default would fetch every
        // dispatched key in the run — one row per dispatched step — to answer a single-key question,
        // and the ForEach admission gate asks it on every step completion while a loop is parked.
        await using var conn = new SqlConnection(_connectionString);
        var exists = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM FlowStepDispatches WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey });

        return exists is not null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One statement: <c>UPDATE TOP … OUTPUT</c> selects and re-stamps the qualifying rows atomically, and
    /// <c>READPAST</c> on the ledger makes a concurrent caller on another replica skip rows this one holds
    /// instead of leasing them a second time. The candidate scan is served by the filtered index
    /// <c>IX_FlowStepDispatches_Unsent</c> (rows with no job id only), so its cost follows the number of
    /// in-flight or stranded dispatches, not the size of the ledger. Ages are measured on
    /// <c>SYSDATETIMEOFFSET()</c>, the clock that stamped <c>DispatchedAt</c>.
    /// </remarks>
    public async Task<IReadOnlyList<UnsentDispatch>> LeaseUnsentDispatchesAsync(TimeSpan minimumAge, int maxCount, CancellationToken ct = default)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();
        await using var conn = new SqlConnection(_connectionString);
        var ageMs = (int)Math.Clamp(minimumAge.TotalMilliseconds, 0, int.MaxValue);
        var rows = await conn.QueryAsync<(Guid RunId, Guid FlowId, string StepKey)>(
            """
            UPDATE TOP (@MaxCount) d
            SET d.DispatchedAt = SYSDATETIMEOFFSET()
            OUTPUT inserted.RunId, r.FlowId, inserted.StepKey
            FROM FlowStepDispatches AS d WITH (ROWLOCK, READPAST)
            INNER JOIN FlowRuns AS r ON r.Id = d.RunId
            WHERE d.DispatchJobId IS NULL
              AND d.DispatchedAt <= DATEADD(MILLISECOND, -@AgeMs, SYSDATETIMEOFFSET())
              AND r.Status = 'Running'
              AND NOT EXISTS (
                  SELECT 1 FROM FlowStepClaims AS c
                  WHERE c.RunId = d.RunId AND c.StepKey = d.StepKey)
              AND NOT EXISTS (
                  SELECT 1 FROM FlowSteps AS s
                  WHERE s.RunId = d.RunId AND s.StepKey = d.StepKey AND s.Status <> 'Pending');
            """,
            new { MaxCount = maxCount, AgeMs = ageMs }).ConfigureAwait(false);

        var leased = new List<UnsentDispatch>();
        foreach (var (runId, flowId, stepKey) in rows)
        {
            leased.Add(new UnsentDispatch(runId, flowId, stepKey));
        }

        return leased;
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

    /// <summary>Most keys bound in one statement: SQL Server allows 2 100 parameters per request.</summary>
    private const int StatusLookupChunkSize = 1000;

    /// <inheritdoc/>
    /// <remarks>
    /// Clustered-key seeks on <c>(RunId, StepKey)</c>, at most <see cref="StatusLookupChunkSize"/> keys per
    /// statement. Each statement is a fresh read-committed (RCSI: statement-snapshot) read on its own
    /// connection, so it sees every write committed before it started — the visibility the engine's frontier
    /// read relies on.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId, IReadOnlyCollection<string> stepKeys)
    {
        var map = new Dictionary<string, StepStatus>(stepKeys.Count, StringComparer.Ordinal);
        if (stepKeys.Count == 0)
        {
            return map;
        }

        await using var conn = new SqlConnection(_connectionString);
        foreach (var chunk in stepKeys.Distinct(StringComparer.Ordinal).Chunk(StatusLookupChunkSize))
        {
            var rows = await conn.QueryAsync<(string StepKey, string Status)>(
                "SELECT StepKey, Status FROM FlowSteps WHERE RunId = @RunId AND StepKey IN @StepKeys",
                new { RunId = runId, StepKeys = chunk }).ConfigureAwait(false);
            foreach (var (stepKey, status) in rows)
            {
                map[stepKey] = ParseStepStatus(status);
            }
        }

        return map;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Served by the filtered index <c>IX_FlowSteps_RunId_Open</c>, which holds only Running and Pending rows;
    /// the predicate below is its filter verbatim, as literals, so the optimizer can match it.
    /// </remarks>
    public async Task<bool> HasRunningOrPendingStepAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var open = await conn.ExecuteScalarAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM FlowSteps
                WHERE RunId = @RunId AND [Status] IN (N'Running', N'Pending')) THEN 1 ELSE 0 END
            """,
            new { RunId = runId }).ConfigureAwait(false);

        return open == 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A clustered-key range seek on <c>(RunId, StepKey LIKE '{loop}.%')</c> — the loop's own rows — grouped by
    /// iteration on the server, so one row travels back. The iteration segment is parsed with guarded string
    /// functions (<c>TRY_CAST</c>, a <c>CASE</c> around every length) because SQL Server may evaluate an
    /// expression before the predicate that would have filtered its row out. Only canonical indices (no
    /// leading zero, at most nine digits) count, which is the form the engine writes. "Terminal" is every
    /// status but Running and Pending — the same verdict <c>ParseStepStatus</c> reaches, which reads an
    /// unrecognised status as Failed — so this count and the status map can never disagree.
    /// </remarks>
    public async Task<int> CountSettledIterationsAsync(Guid runId, string loopStepKey, int iterations, IReadOnlyCollection<string> bodyStepKeys)
    {
        if (iterations <= 0 || bodyStepKeys.Count == 0)
        {
            return 0;
        }

        var body = bodyStepKeys.Distinct(StringComparer.Ordinal).ToArray();
        await using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM (
                SELECT p.Iteration
                FROM FlowSteps AS s
                CROSS APPLY (SELECT SUBSTRING(s.StepKey, @PrefixLength + 1, 256) AS Rest) AS r
                CROSS APPLY (SELECT CHARINDEX(N'.', r.Rest) AS Dot) AS d
                CROSS APPLY (SELECT
                    LEFT(r.Rest, CASE WHEN d.Dot > 0 THEN d.Dot - 1 ELSE 0 END) AS Iteration,
                    CASE WHEN d.Dot > 0 THEN SUBSTRING(r.Rest, d.Dot + 1, 256) ELSE N'' END AS Child) AS p
                WHERE s.RunId = @RunId
                  AND s.StepKey LIKE @Pattern ESCAPE N'\'
                  AND s.[Status] NOT IN (N'Running', N'Pending')
                  AND d.Dot BETWEEN 2 AND 10
                  AND p.Iteration NOT LIKE N'%[^0-9]%'
                  AND (p.Iteration = N'0' OR p.Iteration NOT LIKE N'0%')
                  AND TRY_CAST(p.Iteration AS INT) < @Iterations
                  AND p.Child IN @Body
                GROUP BY p.Iteration
                HAVING COUNT(*) = @BodyCount) AS settled
            """,
            new
            {
                RunId = runId,
                Pattern = EscapeLikePrefix(loopStepKey) + ".%",
                PrefixLength = loopStepKey.Length + 1,
                Iterations = iterations,
                Body = body,
                BodyCount = body.Length
            }).ConfigureAwait(false);
    }

    /// <summary>Escapes <c>LIKE</c> wildcards in <paramref name="value"/> for an <c>ESCAPE '\'</c> pattern.</summary>
    private static string EscapeLikePrefix(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    public async Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<string>(
            "SELECT StepKey FROM FlowStepClaims WHERE RunId = @RunId",
            new { RunId = runId });

        return rows.AsList();
    }

    /// <inheritdoc/>
    public async Task<bool> IsStepClaimedAsync(Guid runId, string stepKey)
    {
        // Point lookup on the (RunId, StepKey) primary key. The interface default would fetch every
        // claimed key in the run — one row per executed step, since claims are never released on
        // terminal statuses — to answer a single-key question.
        await using var conn = new SqlConnection(_connectionString);
        var exists = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM FlowStepClaims WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey });

        return exists is not null;
    }

    public async Task<bool> TryClaimStepAsync(Guid runId, string stepKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        // A bare MERGE is not atomic between its match probe and its INSERT: two concurrent claimers
        // both see "not matched" and the loser throws a PK violation instead of returning false.
        // HOLDLOCK makes the probe take a serializable key-range lock and UPDLOCK takes it in update
        // mode, so the second claimer blocks until the first commits and then matches — the same
        // pattern, and the same deadlock reasoning, as SqlFlowStore.SaveAsync. The catch covers
        // anything that still races through: losing the insert means "already claimed".
        try
        {
            var affected = await conn.ExecuteScalarAsync<int>(
                """
                MERGE FlowStepClaims WITH (HOLDLOCK, UPDLOCK) AS target
                USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
                ON target.RunId = source.RunId AND target.StepKey = source.StepKey
                WHEN NOT MATCHED THEN
                    INSERT (RunId, StepKey, ClaimedAt) VALUES (@RunId, @StepKey, SYSDATETIMEOFFSET());
                SELECT @@ROWCOUNT;
                """,
                new { RunId = runId, StepKey = stepKey });

            return affected > 0;
        }
        catch (SqlException ex) when (SqlUniqueViolation.Matches(ex))
        {
            return false;
        }
    }

    public async Task ReleaseStepClaimAsync(Guid runId, string stepKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM FlowStepClaims WHERE RunId = @RunId AND StepKey = @StepKey",
            new { RunId = runId, StepKey = stepKey });
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ReleaseOrphanedClaimsAsync(Guid runId, TimeSpan minimumAge)
    {
        await using var conn = new SqlConnection(_connectionString);

        // The age is measured on SYSDATETIMEOFFSET(), the clock that stamped ClaimedAt. UPDLOCK, HOLDLOCK
        // on the status probe takes a key-range lock, so a first status write racing this statement
        // either commits first (the claim is kept) or waits until this transaction is done. Claims and
        // ledger rows are removed in one transaction, so a crash cannot leave "no claim, ledger row".
        var ageMs = (int)Math.Clamp(minimumAge.TotalMilliseconds, 0, int.MaxValue);
        var keys = await conn.QueryAsync<string>(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @Released TABLE (StepKey NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);

            DELETE c
            OUTPUT deleted.StepKey INTO @Released (StepKey)
            FROM FlowStepClaims AS c
            WHERE c.RunId = @RunId
              AND c.ClaimedAt <= DATEADD(MILLISECOND, -@AgeMs, SYSDATETIMEOFFSET())
              AND NOT EXISTS (
                  SELECT 1 FROM FlowSteps AS s WITH (UPDLOCK, HOLDLOCK)
                  WHERE s.RunId = c.RunId AND s.StepKey = c.StepKey);

            DELETE d
            FROM FlowStepDispatches AS d
            INNER JOIN @Released AS r ON r.StepKey = d.StepKey
            WHERE d.RunId = @RunId;

            COMMIT TRANSACTION;

            SELECT StepKey FROM @Released;
            """,
            new { RunId = runId, AgeMs = ageMs }).ConfigureAwait(false);

        return keys.AsList();
    }

    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason)
    {
        await RecordSkippedStepAsync(runId, stepKey, stepType, reason, evaluationTraceJson: null).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One round trip and one transaction (RFC 2.0 P-1(b)). The engine claims the step first and then
    /// calls this; before 2.0 the skip was a start write, a completion write and a trace write in up to
    /// three transactions, and a crash between them left a claim with no <c>Skipped</c> row, which the
    /// completion gate reads as in-flight work for ever.
    /// </remarks>
    public async Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason, string? evaluationTraceJson)
    {
        await using var conn = new SqlConnection(_connectionString);
        var trace = string.IsNullOrEmpty(evaluationTraceJson) ? null : evaluationTraceJson;
        var tokens = _tokenIndex?.TokenizeStep(stepKey, reason, null);
        var parameters = new { RunId = runId, StepKey = stepKey, StepType = stepType, Reason = reason, EvaluationTraceJson = trace, SearchTokens = tokens };
        if (tokens is null)
        {
            await conn.ExecuteAsync(RecordSkippedStepSql, parameters).ConfigureAwait(false);
        }
        else
        {
            // Same round trip; the token insert is its own best-effort statement after the commit.
            await conn.ExecuteAsync(RecordSkippedStepSql + """

                DECLARE @SearchStartedAt DATETIMEOFFSET = (SELECT StartedAt FROM FlowRuns WHERE Id = @RunId);
                DECLARE @SearchRunId UNIQUEIDENTIFIER = @RunId;
                """ + SqlServerRunSearchTokenIndex.InsertTokensSql, parameters).ConfigureAwait(false);
        }

        if (_externalIndex is { } index)
        {
            var document = new StepSearchDocument(runId, stepKey, StepStatus.Skipped.ToString(), reason, null);
            await IndexBestEffortAsync(() => index.IndexStepAsync(document, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The skip as one transaction: a new attempt row born <c>Skipped</c>, and the step row upserted to
    /// <c>Skipped</c> — the same rows the old start + complete + trace sequence produced. The attempt
    /// number is allocated under <c>UPDLOCK, HOLDLOCK</c>, as in <see cref="RecordStepStartAsync"/>.
    /// A <see langword="null"/> trace keeps the step row's previous trace, as the old sequence did.
    /// </summary>
    private const string RecordSkippedStepSql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            DECLARE @AttemptNo INT;
            SELECT @AttemptNo = ISNULL(MAX(AttemptNo), 0) + 1
            FROM FlowStepAttempts WITH (UPDLOCK, HOLDLOCK)
            WHERE RunId = @RunId AND StepKey = @StepKey;

            INSERT INTO FlowStepAttempts (RunId, StepKey, AttemptNo, StepType, Status, ErrorMessage, EvaluationTraceJson, StartedAt, CompletedAt)
            VALUES (@RunId, @StepKey, @AttemptNo, @StepType, 'Skipped', @Reason, @EvaluationTraceJson, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

            MERGE FlowSteps WITH (HOLDLOCK, UPDLOCK) AS target
            USING (SELECT @RunId AS RunId, @StepKey AS StepKey) AS source
            ON target.RunId = source.RunId AND target.StepKey = source.StepKey
            WHEN MATCHED THEN
                UPDATE SET
                    StepType = @StepType,
                    InputJson = NULL,
                    OutputJson = NULL,
                    ErrorMessage = @Reason,
                    JobId = NULL,
                    Status = 'Skipped',
                    StartedAt = SYSDATETIMEOFFSET(),
                    CompletedAt = SYSDATETIMEOFFSET(),
                    EvaluationTraceJson = ISNULL(@EvaluationTraceJson, target.EvaluationTraceJson)
            WHEN NOT MATCHED THEN
                INSERT (RunId, StepKey, StepType, Status, ErrorMessage, EvaluationTraceJson, StartedAt, CompletedAt)
                VALUES (@RunId, @StepKey, @StepType, 'Skipped', @Reason, @EvaluationTraceJson, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

            COMMIT TRANSACTION;
            """;

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
        // HOLDLOCK, UPDLOCK: see SqlFlowStore.SaveAsync — keeps a concurrent first configure of one run
        // from racing past the match probe into a PK violation.
        await conn.ExecuteAsync(
            """
            MERGE FlowRunControls WITH (HOLDLOCK, UPDLOCK) AS target
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
                IdempotencyKey = IdempotencyKeyStorage.ToStorageForm(idempotencyKey),
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

    /// <inheritdoc/>
    /// <remarks>
    /// Does not apply to a run whose cancel was already requested: a user cancel that landed between the
    /// caller's deadline check and this write wins, so the run stays <c>Cancelled</c> and a later retry's
    /// <see cref="ExtendDeadlineAsync"/> cannot clear it (it only un-latches timeout-induced cancels).
    /// </remarks>
    public async Task<bool> MarkTimedOutAsync(Guid runId, string? reason)
    {
        await using var conn = new SqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            UPDATE FlowRunControls
            SET TimedOutAtUtc = SYSDATETIMEOFFSET(),
                CancelRequested = 1,
                CancelReason = COALESCE(@Reason, CancelReason, 'Run timed out.'),
                CancelRequestedAtUtc = COALESCE(CancelRequestedAtUtc, SYSDATETIMEOFFSET())
            WHERE RunId = @RunId
              AND TimedOutAtUtc IS NULL
              AND CancelRequested = 0
            """,
            new { RunId = runId, Reason = reason });

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> ExtendDeadlineAsync(Guid runId, DateTimeOffset? newTimeoutAtUtc)
    {
        await using var conn = new SqlConnection(_connectionString);
        // Every SET expression reads the pre-update row, so the CASEs evaluate the OLD TimedOutAtUtc:
        // un-latch the cancel fields only when the termination was timeout-induced, preserving a
        // genuine user cancellation. TimedOutAtUtc is cleared unconditionally.
        var affected = await conn.ExecuteAsync(
            """
            UPDATE FlowRunControls
            SET TimeoutAtUtc = @NewTimeoutAtUtc,
                CancelRequested      = CASE WHEN TimedOutAtUtc IS NOT NULL THEN 0    ELSE CancelRequested END,
                CancelReason         = CASE WHEN TimedOutAtUtc IS NOT NULL THEN NULL ELSE CancelReason END,
                CancelRequestedAtUtc = CASE WHEN TimedOutAtUtc IS NOT NULL THEN NULL ELSE CancelRequestedAtUtc END,
                TimedOutAtUtc        = NULL
            WHERE RunId = @RunId
            """,
            new { RunId = runId, NewTimeoutAtUtc = newTimeoutAtUtc });

        return affected > 0;
    }

    public async Task<Guid?> FindRunIdByIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey)
    {
        await using var conn = new SqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey);

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

    /// <inheritdoc/>
    public async Task<bool> ReleaseIdempotencyKeyAsync(Guid flowId, string triggerKey, string idempotencyKey, Guid runId)
    {
        await using var conn = new SqlConnection(_connectionString);
        var affected = await conn.ExecuteAsync(
            """
            DELETE FROM FlowIdempotencyKeys
            WHERE FlowId = @FlowId
              AND TriggerKey = @TriggerKey
              AND IdempotencyKey = @IdempotencyKey
              AND RunId = @RunId
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
        await using var conn = new SqlConnection(_connectionString);
        var normalizedTriggerKey = triggerKey.Trim().ToLowerInvariant();
        var normalizedIdempotencyKey = IdempotencyKeyStorage.Normalize(idempotencyKey);

        // Same race and the same fix as TryClaimStepAsync: HOLDLOCK + UPDLOCK serialise concurrent
        // registrations of one key, and a duplicate-key error still means "someone else won".
        try
        {
            var affected = await conn.ExecuteScalarAsync<int>(
                """
                MERGE FlowIdempotencyKeys WITH (HOLDLOCK, UPDLOCK) AS target
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
        catch (SqlException ex) when (SqlUniqueViolation.Matches(ex))
        {
            return false;
        }
    }

    public async Task CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The built-in token index is swept by its own background service (tokens of deleted runs are
        // never returned by a search: the probe joins FlowRuns). An external index is told which runs went.
        if (_externalIndex is { } index)
        {
            var deleted = (await conn.QueryAsync<Guid>(CleanupReturningIdsSql, new { CutoffUtc = cutoffUtc }, tx).ConfigureAwait(false)).AsList();
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (deleted.Count > 0)
            {
                try
                {
                    await index.RemoveRunsAsync(deleted, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Best effort: an orphaned document only costs the index space; the hydrate joins FlowRuns.
                }
            }

            return;
        }

        await conn.ExecuteAsync(CleanupSql, new { CutoffUtc = cutoffUtc }, tx).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retention: every table keyed by run, children first; the runs themselves follow in <see cref="CleanupSql"/>.</summary>
    private const string CleanupChildrenSql = """
            DELETE fsc
            FROM FlowStepClaims fsc
            INNER JOIN FlowRuns fr ON fr.Id = fsc.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            -- FlowStepDispatches and FlowSignalWaiters are keyed (RunId, StepKey) with no foreign
            -- key to FlowRuns, so deleting the run does NOT cascade to them. Without these two
            -- statements both tables accumulate one row per dispatched step / parked signal for
            -- every run ever executed and are never reclaimed by retention.
            DELETE fsd
            FROM FlowStepDispatches fsd
            INNER JOIN FlowRuns fr ON fr.Id = fsd.RunId
            WHERE fr.CompletedAt IS NOT NULL
              AND fr.CompletedAt < @CutoffUtc;

            DELETE fsw
            FROM FlowSignalWaiters fsw
            INNER JOIN FlowRuns fr ON fr.Id = fsw.RunId
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

            """;

    private const string CleanupSql = CleanupChildrenSql
        + "DELETE FROM FlowRuns WHERE CompletedAt IS NOT NULL AND CompletedAt < @CutoffUtc;";

    private const string CleanupReturningIdsSql = CleanupChildrenSql
        + "DELETE FROM FlowRuns OUTPUT deleted.Id WHERE CompletedAt IS NOT NULL AND CompletedAt < @CutoffUtc;";

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
