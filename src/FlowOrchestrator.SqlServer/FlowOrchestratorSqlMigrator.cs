using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Hosted service that runs idempotent SQL schema migrations at startup.
/// Creates the <c>FlowDefinitions</c>, <c>FlowRuns</c>, <c>FlowSteps</c>,
/// <c>FlowStepAttempts</c>, <c>FlowOutputs</c>, and related tables if they do not already exist.
/// Safe to run on every startup — all statements use <c>IF NOT EXISTS</c> guards.
/// </summary>
/// <remarks>
/// <para><b>Run-search structures (2.0).</b> Two non-persisted computed columns on <c>FlowRuns</c>
/// (<c>IdText</c>, <c>RunSearchText</c>) are added synchronously — a metadata-only change, no row is
/// rewritten. Their two indexes (<c>IX_FlowRuns_IdText</c>, <c>IX_FlowRuns_StartedAt_Search</c>, about
/// 56 MB per 200k runs, 0.9 s to build measured) are built:</para>
/// <list type="bullet">
/// <item>in the background with <c>ONLINE = ON</c> on editions that support it (Azure SQL Database,
/// Managed Instance, Enterprise / Developer) — startup is not delayed; where <c>WAIT_AT_LOW_PRIORITY</c> is
/// available (Azure, SQL Server 2022+) the build's short table locks never make writers queue behind it;</item>
/// <item>synchronously (offline) on other editions while <c>FlowRuns</c> has at most
/// <see cref="OfflineIndexBuildRowLimit"/> rows;</item>
/// <item>not at all above that on editions without online builds: a warning names the two statements to
/// run in a maintenance window.</item>
/// </list>
/// <para>Until an index exists, <see cref="SqlFlowRunStore"/> uses query shapes that do not need it, so
/// search stays correct, only slower.</para>
/// <para><b>Open-step index (2.0, #189 P0-1).</b> <c>IX_FlowSteps_RunId_Open</c> (filtered on
/// <c>Status IN ('Running', 'Pending')</c>) follows the same policy against the <c>FlowSteps</c> row count, and is
/// built before the run-search indexes on the background path. Until it exists the completion gate's open-row
/// check reads each run's rows through the primary key: correct, only slower.</para>
/// <para><b>Several hosts starting together.</b> The index build runs under an exclusive application lock: on
/// the online path a host that finds another one building leaves it to that host (event 7209); on the
/// offline path it waits for that build and then finds nothing to do.</para>
/// <para>With <c>UseRunSearchIndex()</c> the token tables are created as well (empty; the back-fill runs
/// in the background).</para>
/// </remarks>
public sealed partial class FlowOrchestratorSqlMigrator : IHostedService, IDisposable
{
    /// <summary>
    /// Largest <c>FlowRuns</c> row count for which the search indexes are built synchronously and offline
    /// on an edition without online index builds (roughly ten seconds of startup on such a table).
    /// </summary>
    public const long OfflineIndexBuildRowLimit = 2_000_000;

    private readonly string _connectionString;
    private readonly ILogger<FlowOrchestratorSqlMigrator> _logger;
    private readonly bool _runSearchIndexEnabled;
    private readonly CancellationTokenSource _stopping = new();
    private Task _backgroundIndexBuild = Task.CompletedTask;

    /// <summary>Creates the migrator without the opt-in run-search token tables.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    /// <param name="logger">Logger.</param>
    public FlowOrchestratorSqlMigrator(string connectionString, ILogger<FlowOrchestratorSqlMigrator> logger)
        : this(connectionString, logger, runSearchIndexEnabled: false)
    {
    }

    /// <summary>Creates the migrator.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="runSearchIndexEnabled">Also create the run-search token tables (<c>UseRunSearchIndex()</c>).</param>
    public FlowOrchestratorSqlMigrator(string connectionString, ILogger<FlowOrchestratorSqlMigrator> logger, bool runSearchIndexEnabled)
    {
        _connectionString = connectionString;
        _logger = logger;
        _runSearchIndexEnabled = runSearchIndexEnabled;
    }

    /// <summary>
    /// The background build of the open-step and run-search indexes started by <see cref="StartAsync"/>, or a completed task
    /// when none was started. Completes when the build ends; it never faults (a failed build is logged and
    /// retried on the next start).
    /// </summary>
    /// <remarks>
    /// For code that runs the migrator outside a host (tests, benchmarks, tooling) and must not write to
    /// <c>FlowRuns</c> while the online build is still running.
    /// </remarks>
    public Task BackgroundIndexBuild => _backgroundIndexBuild;

    /// <summary>
    /// Test seam: overrides the edition check that picks the online (background) or offline (synchronous)
    /// index build, so the offline path can run on a Developer-edition test server.
    /// </summary>
    internal bool? OnlineIndexBuildOverride { get; init; }

    /// <summary>Applies the schema; starts the background index build where supported.</summary>
    /// <param name="cancellationToken">Host start-up cancellation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogMigrationsStarting();
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = MigrationSql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Separate batch: the computed columns reference FlowRuns columns, which must exist when it compiles.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = RunSearchColumnsSql + (_runSearchIndexEnabled ? RunSearchTokenTablesSql : string.Empty);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureIndexesAsync(conn, cancellationToken).ConfigureAwait(false);
            LogMigrationsCompleted();
        }
        catch (Exception ex)
        {
            LogMigrationsFailed(ex);
            throw;
        }
    }

    /// <summary>Cancels a background index build that is still running (SQL Server rolls it back; the next start retries).</summary>
    /// <param name="cancellationToken">Host shutdown budget.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _backgroundIndexBuild.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown budget exhausted: the server rolls the build back on its own.
        }
    }

    /// <summary>
    /// Ensures the indexes that are too large to build inside the migration batch — the run-search indexes on
    /// <c>FlowRuns</c> and the open-step index on <c>FlowSteps</c> — with one policy for both: online and in
    /// the background where the edition supports it; otherwise synchronously while the table has at most
    /// <see cref="OfflineIndexBuildRowLimit"/> rows; otherwise not at all, with a warning naming the DDL.
    /// </summary>
    private async Task EnsureIndexesAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = IndexProbeSql;
        int edition, compatibility, majorVersion;
        long runRows, stepRows;
        bool searchMissing, openStepMissing;
        await using (var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            edition = reader.GetInt32(0);
            runRows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            searchMissing = reader.GetInt32(2) > 0;
            compatibility = reader.GetInt32(3);
            majorVersion = reader.GetInt32(4);
            stepRows = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            openStepMissing = reader.GetInt32(6) > 0;
        }

        if (_runSearchIndexEnabled && compatibility < 130)
            LogTokenIndexNeedsCompatibilityLevel(compatibility);

        if (!searchMissing && !openStepMissing)
            return;

        if (OnlineIndexBuildOverride ?? SupportsOnlineIndexBuild(edition))
        {
            var lowPriority = SupportsLowPriorityIndexBuild(edition, majorVersion);
            if (openStepMissing)
                LogBuildingOpenStepIndexOnline(stepRows);
            if (searchMissing)
                LogBuildingIndexesOnline(runRows);
            _backgroundIndexBuild = Task.Run(
                () => BuildIndexesInBackgroundAsync(openStepMissing, searchMissing, lowPriority, _stopping.Token),
                CancellationToken.None);
            return;
        }

        if (openStepMissing)
        {
            if (stepRows <= OfflineIndexBuildRowLimit)
            {
                // Waits for another host's build instead of racing it; its IF NOT EXISTS guard then skips it.
                await BuildIndexesExclusiveAsync(OpenStepIndexLock, BuildOpenStepIndexSql(online: false), OfflineIndexBuildTimeoutSeconds * 1000, OfflineIndexBuildTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                LogOpenStepIndexSkipped(stepRows, BuildOpenStepIndexSql(online: false));
            }
        }

        if (searchMissing)
        {
            if (runRows <= OfflineIndexBuildRowLimit)
            {
                await BuildIndexesExclusiveAsync(RunSearchIndexLock, BuildRunSearchIndexesSql(online: false), OfflineIndexBuildTimeoutSeconds * 1000, OfflineIndexBuildTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                LogIndexesSkipped(runRows, BuildRunSearchIndexesSql(online: false));
            }
        }
    }

    /// <summary>
    /// The online path: builds the missing indexes one after the other, the small open-step index first, each
    /// under its own application lock so a host that finds another one building leaves that build to it.
    /// </summary>
    private async Task BuildIndexesInBackgroundAsync(bool openStep, bool runSearch, bool lowPriority, CancellationToken cancellationToken)
    {
        if (openStep)
        {
            try
            {
                if (await BuildIndexesExclusiveAsync(OpenStepIndexLock, BuildOpenStepIndexSql(online: true, lowPriority), lockTimeoutMs: 0, commandTimeoutSeconds: 0, cancellationToken).ConfigureAwait(false))
                    LogOpenStepIndexBuilt();
                else
                    LogOpenStepIndexBuildInFlightElsewhere();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // Host stopping: retried on the next start.
            }
            catch (SqlException ex)
            {
                // The completion gate's open-row check stays correct without the index, only slower; the
                // next start retries.
                LogOpenStepIndexBuildFailed(ex);
            }
        }

        if (!runSearch)
            return;

        try
        {
            if (await BuildIndexesExclusiveAsync(RunSearchIndexLock, BuildRunSearchIndexesSql(online: true, lowPriority), lockTimeoutMs: 0, commandTimeoutSeconds: 0, cancellationToken).ConfigureAwait(false))
                LogIndexesBuilt();
            else
                LogIndexBuildInFlightElsewhere();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host stopping: retried on the next start.
        }
        catch (SqlException ex)
        {
            // Another replica building the same index, or a transient failure: search keeps its fallback
            // shapes, and the next start retries.
            LogIndexBuildFailed(ex);
        }
    }

    /// <summary>Application-lock resource serialising the run-search index builds across hosts.</summary>
    private const string RunSearchIndexLock = "FlowOrchestrator.RunSearchIndexes";

    /// <summary>Application-lock resource serialising the open-step index build across hosts.</summary>
    private const string OpenStepIndexLock = "FlowOrchestrator.OpenStepIndex";

    /// <summary>
    /// Runs <paramref name="sql"/> while holding the exclusive application lock <paramref name="lockResource"/>,
    /// so two hosts starting together build an index once instead of racing: the loser of a race failed with
    /// error 1913 ("an index ... already exists") because both passed the <c>IF NOT EXISTS</c> guard before
    /// either committed. That aborted the loser's start on the offline path and logged a false build failure
    /// on the online one.
    /// </summary>
    /// <param name="lockResource">The application-lock resource for this group of indexes.</param>
    /// <param name="sql">The guarded DDL.</param>
    /// <param name="lockTimeoutMs">How long to wait for another host's build; 0 gives up at once.</param>
    /// <param name="commandTimeoutSeconds">Timeout of the build itself; 0 for none.</param>
    /// <param name="cancellationToken">Cancels the wait or the build (SQL Server rolls it back).</param>
    /// <returns><see langword="false"/> when another host holds the lock and <paramref name="lockTimeoutMs"/> ran out.</returns>
    /// <remarks>
    /// The lock is session-owned and the connection is not pooled: closing it ends the session, which releases
    /// the lock on every path, cancellation and errors included. A pooled connection would keep the session,
    /// and with it the lock, alive in the pool.
    /// </remarks>
    internal async Task<bool> BuildIndexesExclusiveAsync(string lockResource, string sql, int lockTimeoutMs, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var unpooled = new SqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString;
        await using var conn = new SqlConnection(unpooled);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var acquire = conn.CreateCommand())
        {
            acquire.CommandText = """
                DECLARE @result INT;
                EXEC @result = sp_getapplock @Resource = @lockResource, @LockMode = N'Exclusive',
                    @LockOwner = N'Session', @LockTimeout = @lockTimeoutMs;
                SELECT @result;
                """;
            acquire.Parameters.AddWithValue("@lockResource", lockResource);
            acquire.Parameters.AddWithValue("@lockTimeoutMs", lockTimeoutMs);
            acquire.CommandTimeout = lockTimeoutMs == 0 ? 30 : (lockTimeoutMs / 1000) + 30;
            if (Convert.ToInt32(await acquire.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) < 0)
                return false;
        }

        await using var build = conn.CreateCommand();
        build.CommandText = sql;
        build.CommandTimeout = commandTimeoutSeconds;
        await build.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>How long a low-priority online build waits for each of its table locks before giving up.</summary>
    internal const int LowPriorityMaxDurationMinutes = 5;

    /// <summary>Budget of the synchronous offline build, and of waiting for another host's.</summary>
    private const int OfflineIndexBuildTimeoutSeconds = 600;

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    /// <summary>Azure SQL Database (5), Managed Instance (8) and Enterprise / Developer / Evaluation (3) build indexes online.</summary>
    internal static bool SupportsOnlineIndexBuild(int engineEdition) => engineEdition is 3 or 5 or 8;

    /// <summary>
    /// <c>WAIT_AT_LOW_PRIORITY</c> on <c>CREATE INDEX</c>: Azure SQL Database (5), Managed Instance (8), and
    /// SQL Server 2022 (major version 16) or later for Enterprise / Developer (3).
    /// </summary>
    internal static bool SupportsLowPriorityIndexBuild(int engineEdition, int productMajorVersion) =>
        engineEdition is 5 or 8 || (engineEdition == 3 && productMajorVersion >= 16);

    /// <summary>The DDL for the run-search indexes, each guarded so a partial earlier build is completed.</summary>
    /// <param name="online">Build with <c>ONLINE = ON</c>.</param>
    /// <param name="lowPriority">
    /// Add <c>WAIT_AT_LOW_PRIORITY</c>. An online build still needs a short S lock on <c>FlowRuns</c> at its
    /// start and end; a plain request queues behind any open write transaction and every later writer queues
    /// behind it, so one long transaction (a large retention sweep) stalls all run writes. At low priority the
    /// build waits without blocking anyone and gives up after <see cref="LowPriorityMaxDurationMinutes"/>
    /// minutes (warning 7206); the next start retries.
    /// </param>
    internal static string BuildRunSearchIndexesSql(bool online, bool lowPriority = false)
    {
        var with = !online ? string.Empty
            : lowPriority ? $" WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = {LowPriorityMaxDurationMinutes} MINUTES, ABORT_AFTER_WAIT = SELF)))"
            : " WITH (ONLINE = ON)";
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_IdText' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX [IX_FlowRuns_IdText] ON [FlowRuns]([IdText]){with};
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowRuns_StartedAt_Search' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX [IX_FlowRuns_StartedAt_Search] ON [FlowRuns]([StartedAt] DESC)
                INCLUDE ([RunSearchText], [FlowId], [Status]){with};
            """;
    }

    /// <summary>
    /// The DDL for <c>IX_FlowSteps_RunId_Open</c>, the filtered index behind
    /// <see cref="SqlFlowRunStore.HasRunningOrPendingStepAsync"/> (#189 P0-1): only the Running and Pending rows,
    /// so the completion gate's open-row check costs O(open steps).
    /// </summary>
    /// <param name="online">Build with <c>ONLINE = ON</c>.</param>
    /// <param name="lowPriority">Add <c>WAIT_AT_LOW_PRIORITY</c>; see <see cref="BuildRunSearchIndexesSql"/>.</param>
    /// <remarks>
    /// Built outside the migration batch because <c>FlowSteps</c> is the hottest table: an offline build reads
    /// the whole table under a lock that blocks every step write, which on an upgraded 1.x database would stall
    /// the first start and every replica starting beside it. The store's query is correct without the index.
    /// </remarks>
    internal static string BuildOpenStepIndexSql(bool online, bool lowPriority = false)
    {
        var with = !online ? string.Empty
            : lowPriority ? $" WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = {LowPriorityMaxDurationMinutes} MINUTES, ABORT_AFTER_WAIT = SELF)))"
            : " WITH (ONLINE = ON)";
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowSteps_RunId_Open' AND object_id = OBJECT_ID(N'[FlowSteps]'))
                CREATE INDEX [IX_FlowSteps_RunId_Open] ON [FlowSteps]([RunId]) INCLUDE ([StepKey])
                WHERE [Status] IN (N'Running', N'Pending'){with};
            """;
    }

    private const string IndexProbeSql = """
        SELECT
            CAST(SERVERPROPERTY('EngineEdition') AS INT),
            (SELECT CAST(SUM(p.rows) AS BIGINT) FROM sys.partitions AS p WHERE p.object_id = OBJECT_ID(N'[FlowRuns]') AND p.index_id IN (0, 1)),
            (SELECT COUNT(*) FROM (VALUES (N'IX_FlowRuns_IdText'), (N'IX_FlowRuns_StartedAt_Search')) AS wanted(name)
             WHERE NOT EXISTS (SELECT 1 FROM sys.indexes AS i WHERE i.name = wanted.name AND i.object_id = OBJECT_ID(N'[FlowRuns]'))),
            (SELECT CAST(compatibility_level AS INT) FROM sys.databases WHERE database_id = DB_ID()),
            CAST(SERVERPROPERTY('ProductMajorVersion') AS INT),
            (SELECT CAST(SUM(p.rows) AS BIGINT) FROM sys.partitions AS p WHERE p.object_id = OBJECT_ID(N'[FlowSteps]') AND p.index_id IN (0, 1)),
            CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowSteps_RunId_Open' AND object_id = OBJECT_ID(N'[FlowSteps]')) THEN 0 ELSE 1 END;
        """;

    /// <summary>
    /// The run-search computed columns. Non-persisted: adding them changes metadata only. <c>RunSearchText</c>
    /// is lower-cased and binary-collated so a <c>%term%</c> match is one binary compare per row.
    /// </summary>
    private const string RunSearchColumnsSql = """
        IF COL_LENGTH(N'FlowRuns', N'IdText') IS NULL
            ALTER TABLE [FlowRuns] ADD [IdText] AS CONVERT(CHAR(36), [Id]);

        IF COL_LENGTH(N'FlowRuns', N'RunSearchText') IS NULL
            ALTER TABLE [FlowRuns] ADD [RunSearchText] AS
                CAST(LOWER(CONCAT(CONVERT(CHAR(36), [Id]), N'|', [FlowName], N'|', [TriggerKey], N'|', [Status], N'|', [BackgroundJobId])) AS NVARCHAR(800))
                COLLATE Latin1_General_100_BIN2;

        """;

    /// <summary>The opt-in token table and its back-fill state (one row).</summary>
    private const string RunSearchTokenTablesSql = """
        IF OBJECT_ID(N'FlowRunSearchTokens') IS NULL
            CREATE TABLE [FlowRunSearchTokens] (
                [Token]     NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                [StartedAt] DATETIMEOFFSET   NOT NULL,
                [RunId]     UNIQUEIDENTIFIER NOT NULL,
                [Source]    TINYINT          NOT NULL,
                CONSTRAINT [PK_FlowRunSearchTokens] PRIMARY KEY CLUSTERED ([Token], [StartedAt] DESC, [RunId], [Source])
                    WITH (IGNORE_DUP_KEY = ON)
            );

        IF OBJECT_ID(N'FlowRunSearchIndexState') IS NULL
            CREATE TABLE [FlowRunSearchIndexState] (
                [Id]                     TINYINT          NOT NULL CONSTRAINT [PK_FlowRunSearchIndexState] PRIMARY KEY CHECK ([Id] = 1),
                [EnabledAt]              DATETIMEOFFSET   NOT NULL,
                [BackfillAfterStartedAt] DATETIMEOFFSET   NULL,
                [BackfillAfterId]        UNIQUEIDENTIFIER NULL,
                [BackfillCompletedAt]    DATETIMEOFFSET   NULL
            );

        BEGIN TRY
            IF NOT EXISTS (SELECT 1 FROM [FlowRunSearchIndexState] WHERE [Id] = 1)
                INSERT INTO [FlowRunSearchIndexState] ([Id], [EnabledAt]) VALUES (1, SYSDATETIMEOFFSET());
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;   -- another replica inserted it first
        END CATCH
        """;

    [LoggerMessage(EventId = 7201, Level = LogLevel.Information, Message = "Running FlowOrchestrator SQL migrations...")]
    private partial void LogMigrationsStarting();

    [LoggerMessage(EventId = 7202, Level = LogLevel.Information, Message = "FlowOrchestrator SQL migrations completed.")]
    private partial void LogMigrationsCompleted();

    [LoggerMessage(EventId = 7203, Level = LogLevel.Error, Message = "FlowOrchestrator SQL migration failed.")]
    private partial void LogMigrationsFailed(Exception exception);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Information, Message = "Building the run-search indexes online in the background ({Rows} runs); search uses fallback query shapes until they exist.")]
    private partial void LogBuildingIndexesOnline(long rows);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Information, Message = "Run-search indexes built.")]
    private partial void LogIndexesBuilt();

    [LoggerMessage(EventId = 7209, Level = LogLevel.Information, Message = "Another host is building the run-search indexes; this host leaves the build to it.")]
    private partial void LogIndexBuildInFlightElsewhere();

    [LoggerMessage(EventId = 7206, Level = LogLevel.Warning, Message = "Run-search index build failed; search keeps its fallback query shapes and the next start retries.")]
    private partial void LogIndexBuildFailed(Exception exception);

    [LoggerMessage(EventId = 7207, Level = LogLevel.Warning, Message = "FlowRuns has {Rows} rows and this SQL Server edition cannot build indexes online, so the run-search indexes were not built at startup. Run search works but is slower until you run, in a maintenance window: {Ddl}")]
    private partial void LogIndexesSkipped(long rows, string ddl);

    [LoggerMessage(EventId = 7216, Level = LogLevel.Information, Message = "Building the open-step index IX_FlowSteps_RunId_Open online in the background ({Rows} steps); the completion gate's open-row check reads each run's rows until it exists.")]
    private partial void LogBuildingOpenStepIndexOnline(long rows);

    [LoggerMessage(EventId = 7217, Level = LogLevel.Information, Message = "Open-step index IX_FlowSteps_RunId_Open built.")]
    private partial void LogOpenStepIndexBuilt();

    [LoggerMessage(EventId = 7218, Level = LogLevel.Warning, Message = "Open-step index build failed; the completion gate stays correct, only slower, and the next start retries.")]
    private partial void LogOpenStepIndexBuildFailed(Exception exception);

    [LoggerMessage(EventId = 7219, Level = LogLevel.Information, Message = "Another host is building the open-step index; this host leaves the build to it.")]
    private partial void LogOpenStepIndexBuildInFlightElsewhere();

    [LoggerMessage(EventId = 7220, Level = LogLevel.Warning, Message = "FlowSteps has {Rows} rows and this SQL Server edition cannot build indexes online, so IX_FlowSteps_RunId_Open was not built at startup. The engine stays correct but its completion gate reads each run's rows until you run, in a maintenance window: {Ddl}")]
    private partial void LogOpenStepIndexSkipped(long rows, string ddl);

    [LoggerMessage(EventId = 7208, Level = LogLevel.Warning, Message = "UseRunSearchIndex() needs database compatibility level 130 or later (this database: {CompatibilityLevel}); the token index stays unused and deep search uses LIKE.")]
    private partial void LogTokenIndexNeedsCompatibilityLevel(int compatibilityLevel);

    private const string MigrationSql = """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowDefinitions')
        BEGIN
            CREATE TABLE [FlowDefinitions] (
                [Id]           UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                [Name]         NVARCHAR(256)    NOT NULL,
                [Version]      NVARCHAR(64)     NOT NULL DEFAULT '1.0',
                [ManifestJson] NVARCHAR(MAX)    NULL,
                [IsEnabled]    BIT              NOT NULL DEFAULT 1,
                [CreatedAt]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [UpdatedAt]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowRuns')
        BEGIN
            CREATE TABLE [FlowRuns] (
                [Id]              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                [FlowId]          UNIQUEIDENTIFIER NOT NULL,
                [FlowName]        NVARCHAR(256)    NULL,
                [Status]          NVARCHAR(64)     NOT NULL,
                [TriggerKey]      NVARCHAR(256)    NULL,
                [TriggerDataJson] NVARCHAR(MAX)    NULL,
                [BackgroundJobId] NVARCHAR(128)    NULL,
                [StartedAt]       DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [CompletedAt]     DATETIMEOFFSET   NULL
            );
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowSteps')
        BEGIN
            CREATE TABLE [FlowSteps] (
                [RunId]        UNIQUEIDENTIFIER NOT NULL,
                [StepKey]      NVARCHAR(256)    NOT NULL,
                [StepType]     NVARCHAR(256)    NOT NULL,
                [Status]       NVARCHAR(64)     NOT NULL,
                [InputJson]    NVARCHAR(MAX)    NULL,
                [OutputJson]   NVARCHAR(MAX)    NULL,
                [ErrorMessage] NVARCHAR(MAX)    NULL,
                [JobId]        NVARCHAR(128)    NULL,
                [StartedAt]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [CompletedAt]  DATETIMEOFFSET   NULL,
                CONSTRAINT PK_FlowSteps PRIMARY KEY ([RunId], [StepKey])
            );
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowStepAttempts')
        BEGIN
            CREATE TABLE [FlowStepAttempts] (
                [RunId]        UNIQUEIDENTIFIER NOT NULL,
                [StepKey]      NVARCHAR(256)    NOT NULL,
                [AttemptNo]    INT              NOT NULL,
                [StepType]     NVARCHAR(256)    NOT NULL,
                [Status]       NVARCHAR(64)     NOT NULL,
                [InputJson]    NVARCHAR(MAX)    NULL,
                [OutputJson]   NVARCHAR(MAX)    NULL,
                [ErrorMessage] NVARCHAR(MAX)    NULL,
                [JobId]        NVARCHAR(128)    NULL,
                [StartedAt]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [CompletedAt]  DATETIMEOFFSET   NULL,
                CONSTRAINT PK_FlowStepAttempts PRIMARY KEY ([RunId], [StepKey], [AttemptNo])
            );
        END

        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowRuns')
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_FlowId' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX IX_FlowRuns_FlowId ON [FlowRuns]([FlowId]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_Status' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX IX_FlowRuns_Status ON [FlowRuns]([Status]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_StartedAt' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX IX_FlowRuns_StartedAt ON [FlowRuns]([StartedAt] DESC)
                INCLUDE ([FlowId], [FlowName], [Status], [TriggerKey], [BackgroundJobId], [CompletedAt]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_FlowId_Status_StartedAt' AND object_id = OBJECT_ID(N'[FlowRuns]'))
                CREATE INDEX IX_FlowRuns_FlowId_Status_StartedAt ON [FlowRuns]([FlowId], [Status], [StartedAt] DESC)
                INCLUDE ([FlowName], [TriggerKey], [BackgroundJobId], [CompletedAt]);
        END

        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowSteps')
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowSteps_RunId_StartedAt' AND object_id = OBJECT_ID(N'[FlowSteps]'))
                CREATE INDEX IX_FlowSteps_RunId_StartedAt ON [FlowSteps]([RunId], [StartedAt])
                INCLUDE ([StepKey], [StepType], [Status], [JobId], [CompletedAt]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowSteps_StepKey_RunId' AND object_id = OBJECT_ID(N'[FlowSteps]'))
                CREATE INDEX IX_FlowSteps_StepKey_RunId ON [FlowSteps]([StepKey], [RunId]);
        END

        IF EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowStepAttempts')
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowStepAttempts_RunId_StartedAt' AND object_id = OBJECT_ID(N'[FlowStepAttempts]'))
                CREATE INDEX IX_FlowStepAttempts_RunId_StartedAt ON [FlowStepAttempts]([RunId], [StartedAt])
                INCLUDE ([StepKey], [AttemptNo], [StepType], [Status], [JobId], [CompletedAt]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowStepAttempts_RunId_StepKey_AttemptNo' AND object_id = OBJECT_ID(N'[FlowStepAttempts]'))
                CREATE INDEX IX_FlowStepAttempts_RunId_StepKey_AttemptNo ON [FlowStepAttempts]([RunId], [StepKey], [AttemptNo]);
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowOutputs')
        BEGIN
            CREATE TABLE [FlowOutputs] (
                [RunId]      UNIQUEIDENTIFIER NOT NULL,
                [Key]        NVARCHAR(256)    NOT NULL,
                [ValueJson]  NVARCHAR(MAX)    NULL,
                [CreatedAt]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                CONSTRAINT PK_FlowOutputs PRIMARY KEY ([RunId], [Key])
            );
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowStepClaims')
        BEGIN
            CREATE TABLE [FlowStepClaims] (
                [RunId]      UNIQUEIDENTIFIER NOT NULL,
                [StepKey]    NVARCHAR(256)    NOT NULL,
                [ClaimedAt]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                CONSTRAINT PK_FlowStepClaims PRIMARY KEY ([RunId], [StepKey])
            );
        END

        -- Execution leases (2.0, RFC C1): the claim row says "taken", this row says by whom, until when, and
        -- under which fence. A claim without a lease row is permanent (skip claims, claims written before 2.0).
        -- ExpiresAt is NULL once the lease is no longer live (released, pinned, or its step terminal); the row
        -- is kept so the fence keeps rising across attempts. A new table, so no existing table is altered.
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowStepLeases')
        BEGIN
            CREATE TABLE [FlowStepLeases] (
                [RunId]          UNIQUEIDENTIFIER NOT NULL,
                [StepKey]        NVARCHAR(256)    NOT NULL,
                [Fence]          BIGINT           NOT NULL,
                [Owner]          NVARCHAR(128)    NOT NULL,
                [ExpiresAt]      DATETIMEOFFSET   NULL,
                [RedeliveredAt]  DATETIMEOFFSET   NULL,
                CONSTRAINT PK_FlowStepLeases PRIMARY KEY ([RunId], [StepKey])
            );

            -- Live leases only, for the expired-lease sweep: stays as small as the number of steps executing.
            CREATE INDEX IX_FlowStepLeases_Live ON [FlowStepLeases]([ExpiresAt]) WHERE [ExpiresAt] IS NOT NULL;
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowRunControls')
        BEGIN
            CREATE TABLE [FlowRunControls] (
                [RunId]                UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                [FlowId]               UNIQUEIDENTIFIER NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                [TriggerKey]           NVARCHAR(256)    NOT NULL DEFAULT '',
                [IdempotencyKey]       NVARCHAR(256)    NULL,
                [TimeoutAtUtc]         DATETIMEOFFSET   NULL,
                [CancelRequested]      BIT              NOT NULL DEFAULT 0,
                [CancelReason]         NVARCHAR(MAX)    NULL,
                [CancelRequestedAtUtc] DATETIMEOFFSET   NULL,
                [TimedOutAtUtc]        DATETIMEOFFSET   NULL
            );
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowIdempotencyKeys')
        BEGIN
            CREATE TABLE [FlowIdempotencyKeys] (
                [FlowId]          UNIQUEIDENTIFIER NOT NULL,
                [TriggerKey]      NVARCHAR(256)    NOT NULL,
                [IdempotencyKey]  NVARCHAR(256)    NOT NULL,
                [RunId]           UNIQUEIDENTIFIER NOT NULL,
                [CreatedAtUtc]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                CONSTRAINT PK_FlowIdempotencyKeys PRIMARY KEY ([FlowId], [TriggerKey], [IdempotencyKey])
            );

            CREATE INDEX IX_FlowIdempotencyKeys_RunId ON [FlowIdempotencyKeys]([RunId]);
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowStepDispatches')
        BEGIN
            CREATE TABLE [FlowStepDispatches] (
                [RunId]         UNIQUEIDENTIFIER NOT NULL,
                [StepKey]       NVARCHAR(256)    NOT NULL,
                [DispatchedAt]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [DispatchJobId] NVARCHAR(128)    NULL,
                CONSTRAINT PK_FlowStepDispatches PRIMARY KEY ([RunId], [StepKey])
            );
        END

        -- Rows whose runtime hand-off never returned (no job id), for the unsent-dispatch sweep. Filtered,
        -- so it holds only in-flight or stranded dispatches and stays tiny however large the ledger grows.
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowStepDispatches_Unsent' AND object_id = OBJECT_ID(N'[FlowStepDispatches]'))
            CREATE INDEX IX_FlowStepDispatches_Unsent ON [FlowStepDispatches]([DispatchedAt]) WHERE [DispatchJobId] IS NULL;

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowSignalWaiters')
        BEGIN
            CREATE TABLE [FlowSignalWaiters] (
                [RunId]       UNIQUEIDENTIFIER NOT NULL,
                [StepKey]     NVARCHAR(256)    NOT NULL,
                [SignalName]  NVARCHAR(256)    NOT NULL,
                [CreatedAt]   DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [ExpiresAt]   DATETIMEOFFSET   NULL,
                [DeliveredAt] DATETIMEOFFSET   NULL,
                [PayloadJson] NVARCHAR(MAX)    NULL,
                CONSTRAINT PK_FlowSignalWaiters PRIMARY KEY ([RunId], [StepKey])
            );

            CREATE INDEX IX_FlowSignalWaiters_RunId_SignalName
                ON [FlowSignalWaiters] ([RunId], [SignalName]);
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowScheduleStates')
        BEGIN
            CREATE TABLE [FlowScheduleStates] (
                [JobId]         NVARCHAR(256)    NOT NULL PRIMARY KEY,
                [FlowId]        UNIQUEIDENTIFIER NOT NULL,
                [FlowName]      NVARCHAR(256)    NOT NULL DEFAULT '',
                [TriggerKey]    NVARCHAR(256)    NOT NULL,
                [IsPaused]      BIT              NOT NULL DEFAULT 0,
                [CronOverride]  NVARCHAR(128)    NULL,
                [UpdatedAtUtc]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );
        END

        -- When-clause evaluation trace columns (idempotent ALTER TABLE).
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EvaluationTraceJson' AND Object_ID = Object_ID(N'[FlowSteps]'))
        BEGIN
            ALTER TABLE [FlowSteps] ADD [EvaluationTraceJson] NVARCHAR(MAX) NULL;
        END

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EvaluationTraceJson' AND Object_ID = Object_ID(N'[FlowStepAttempts]'))
        BEGIN
            ALTER TABLE [FlowStepAttempts] ADD [EvaluationTraceJson] NVARCHAR(MAX) NULL;
        END

        -- Re-run lineage column (idempotent ALTER TABLE).
        --
        -- NOTE: the CREATE INDEX must be wrapped in EXEC() so SQL Server defers
        -- column-name resolution to runtime. Without it, the entire batch is
        -- parsed at compile time and the [SourceRunId] reference fails on a
        -- pre-existing FlowRuns table that hasn't yet had the column added —
        -- the IF guard alone is NOT enough because SQL Server compiles the
        -- whole batch up front.
        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'SourceRunId' AND Object_ID = Object_ID(N'[FlowRuns]'))
        BEGIN
            ALTER TABLE [FlowRuns] ADD [SourceRunId] UNIQUEIDENTIFIER NULL;
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WebhookReplayNonces')
        BEGIN
            CREATE TABLE [WebhookReplayNonces] (
                [FlowId]      UNIQUEIDENTIFIER NOT NULL,
                [TriggerKey]  NVARCHAR(256)    NOT NULL,
                [Nonce]       NVARCHAR(256)    NOT NULL,
                [ExpiresAt]   DATETIMEOFFSET   NOT NULL,
                CONSTRAINT PK_WebhookReplayNonces PRIMARY KEY ([FlowId], [TriggerKey], [Nonce])
            );

            CREATE INDEX IX_WebhookReplayNonces_ExpiresAt ON [WebhookReplayNonces]([ExpiresAt]);
        END

        -- Owner token of a replay entry: only the request that recorded it can release it again
        -- (TryForgetAsync). NULL on rows written before the column existed, which never match a release.
        IF COL_LENGTH(N'WebhookReplayNonces', N'RegistrationId') IS NULL
            ALTER TABLE [WebhookReplayNonces] ADD [RegistrationId] UNIQUEIDENTIFIER NULL;

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WebhookRejections')
        BEGIN
            CREATE TABLE [WebhookRejections] (
                [Id]            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [FlowId]        UNIQUEIDENTIFIER NULL,
                [TriggerKey]    NVARCHAR(256)    NULL,
                [ReceivedAt]    DATETIMEOFFSET   NOT NULL,
                [RemoteIp]      NVARCHAR(64)     NULL,
                [Reason]        NVARCHAR(64)     NOT NULL,
                [StatusCode]    INT              NOT NULL,
                [BodyBytes]     INT              NOT NULL,
                [BodyTruncated] NVARCHAR(MAX)    NULL,
                [HeadersJson]   NVARCHAR(MAX)    NULL,
                [Scheme]        NVARCHAR(128)    NULL,
                [ProcessingMs]  FLOAT            NULL,
                [IsAccepted]    BIT              NOT NULL DEFAULT 0
            );

            CREATE INDEX IX_WebhookRejections_FlowId_ReceivedAt
                ON [WebhookRejections]([FlowId], [ReceivedAt]);
            CREATE INDEX IX_WebhookRejections_Reason
                ON [WebhookRejections]([Reason]);
            CREATE INDEX IX_WebhookRejections_ReceivedAt
                ON [WebhookRejections]([ReceivedAt]);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_SourceRunId' AND object_id = OBJECT_ID(N'[FlowRuns]'))
        BEGIN
            EXEC('CREATE INDEX IX_FlowRuns_SourceRunId ON [FlowRuns]([SourceRunId]) WHERE [SourceRunId] IS NOT NULL');
        END

        -- Server registry (2.0): one heartbeat row per live FlowOrchestrator process, for the dashboard's
        -- Servers view. ServerId is the step-lease owner id (FlowStepLeases.Owner). Small: no secondary index.
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowServers')
        BEGIN
            CREATE TABLE [FlowServers] (
                [ServerId]         NVARCHAR(128)  NOT NULL,
                [MachineName]      NVARCHAR(256)  NOT NULL,
                [ProcessId]        INT            NOT NULL,
                [Runtime]          NVARCHAR(64)   NOT NULL,
                [Version]          NVARCHAR(128)  NULL,
                [StartedAt]        DATETIMEOFFSET NOT NULL,
                [LastHeartbeatAt]  DATETIMEOFFSET NOT NULL,
                [ActiveStepCount]  INT            NOT NULL DEFAULT 0,
                [FlowCount]        INT            NOT NULL DEFAULT 0,
                CONSTRAINT PK_FlowServers PRIMARY KEY ([ServerId])
            );
        END

        -- The server that ran each attempt (2.0); NULL on skips and on rows written before the column existed.
        IF COL_LENGTH(N'FlowStepAttempts', N'ServerId') IS NULL
            ALTER TABLE [FlowStepAttempts] ADD [ServerId] NVARCHAR(128) NULL;
        """;
}
