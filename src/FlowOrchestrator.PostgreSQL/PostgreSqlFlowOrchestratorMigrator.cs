using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Hosted service that runs idempotent PostgreSQL schema migrations at startup.
/// Creates the <c>flow_definitions</c>, <c>flow_runs</c>, <c>flow_steps</c>,
/// <c>flow_step_attempts</c>, <c>flow_outputs</c>, and related tables if they do not already exist.
/// Safe to run on every startup — all statements use <c>IF NOT EXISTS</c> guards.
/// </summary>
/// <remarks>
/// <para><b>Run search (2.0).</b> With <c>pg_trgm</c> available, a trigram GIN index on the lower-cased run
/// search expression (<c>ix_flow_runs_run_search_trgm</c>, about 47 MB per 200k runs) is built
/// <c>CONCURRENTLY</c> in the background after startup: no table rewrite, writers are not blocked and
/// startup does not wait. An index left <c>INVALID</c> by an interrupted build is dropped and rebuilt on
/// the next start. Without <c>pg_trgm</c> (not allow-listed on Azure Database for PostgreSQL, or no
/// privilege) every trigram step is skipped with a warning and search runs the same SQL as a
/// sequential scan.</para>
/// <para>The <c>flow_steps.output_json</c> trigram index stays on by default: no measurable write cost at
/// p50 and 200x faster deep search in the default window, at about half the size of the step table.</para>
/// </remarks>
public sealed partial class PostgreSqlFlowOrchestratorMigrator : IHostedService, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlFlowOrchestratorMigrator> _logger;
    private readonly bool _runSearchIndexRequested;
    private readonly CancellationTokenSource _stopping = new();
    private Task _backgroundIndexBuild = Task.CompletedTask;

    /// <summary>Creates the migrator.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    /// <param name="logger">Logger.</param>
    public PostgreSqlFlowOrchestratorMigrator(string connectionString, ILogger<PostgreSqlFlowOrchestratorMigrator> logger)
        : this(connectionString, logger, runSearchIndexRequested: false)
    {
    }

    /// <summary>Creates the migrator.</summary>
    /// <param name="connectionString">The FlowOrchestrator database.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="runSearchIndexRequested">
    /// <c>UseRunSearchIndex()</c> was called. The token index is not implemented on PostgreSQL (the trigram
    /// indexes already answer deep search); the migrator says so once instead of ignoring it silently.
    /// </param>
    public PostgreSqlFlowOrchestratorMigrator(string connectionString, ILogger<PostgreSqlFlowOrchestratorMigrator> logger, bool runSearchIndexRequested)
    {
        _connectionString = connectionString;
        _logger = logger;
        _runSearchIndexRequested = runSearchIndexRequested;
    }

    /// <summary>The background build of the run-search index, if one was started; completes when it ends.</summary>
    internal Task BackgroundIndexBuild => _backgroundIndexBuild;

    /// <summary>Applies the schema; starts the background run-search index build when <c>pg_trgm</c> is available.</summary>
    /// <param name="cancellationToken">Host start-up cancellation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running FlowOrchestrator PostgreSQL migrations...");
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = MigrationSql;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (await TryCreateTrigramIndexesAsync(conn, cancellationToken).ConfigureAwait(false))
                _backgroundIndexBuild = Task.Run(() => BuildRunSearchIndexInBackgroundAsync(_stopping.Token), CancellationToken.None);
            if (_runSearchIndexRequested)
                LogTokenIndexNotSupported();

            _logger.LogInformation("FlowOrchestrator PostgreSQL migrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlowOrchestrator PostgreSQL migration failed.");
            throw;
        }
    }

    /// <summary>Cancels a background index build that is still running (PostgreSQL leaves it INVALID; the next start rebuilds it).</summary>
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
            // Shutdown budget exhausted.
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Creates <c>pg_trgm</c> GIN indexes that accelerate the substring (<c>ILIKE</c>)
    /// run-search query. Best-effort: the <c>pg_trgm</c> extension requires a privilege
    /// that some managed PostgreSQL roles lack, so any failure here is logged as a warning
    /// and swallowed — run search still works (the planner falls back to a sequential scan).
    /// </summary>
    /// <returns><see langword="true"/> when <c>pg_trgm</c> and the trigram indexes are in place.</returns>
    /// <remarks>
    /// Kept out of the mandatory <see cref="MigrationSql"/> batch so a missing-privilege
    /// failure cannot abort the schema migration and prevent the host from starting.
    /// </remarks>
    private async Task<bool> TryCreateTrigramIndexesAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = TrigramSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("FlowOrchestrator PostgreSQL pg_trgm search indexes ensured.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "FlowOrchestrator PostgreSQL pg_trgm index creation was skipped (extension unavailable or insufficient privilege). Run search remains functional via sequential scan.");
            return false;
        }
    }

    /// <summary>
    /// Builds <c>ix_flow_runs_run_search_trgm</c> with <c>CREATE INDEX CONCURRENTLY</c>, first dropping an
    /// <c>INVALID</c> leftover of an interrupted build. Each statement runs as its own command:
    /// <c>CONCURRENTLY</c> refuses to run inside a (multi-statement, implicit) transaction.
    /// </summary>
    private async Task BuildRunSearchIndexInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool? valid;
            await using (var probe = conn.CreateCommand())
            {
                probe.CommandText = RunSearchIndexStateSql;
                valid = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as bool?;
            }

            if (valid == true)
                return;

            if (valid == false)
            {
                await using var drop = conn.CreateCommand();
                drop.CommandText = "DROP INDEX CONCURRENTLY IF EXISTS ix_flow_runs_run_search_trgm";
                drop.CommandTimeout = 0;
                await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            LogBuildingRunSearchIndex();
            await using (var build = conn.CreateCommand())
            {
                build.CommandText = CreateRunSearchIndexSql;
                build.CommandTimeout = 0;
                await build.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            LogRunSearchIndexBuilt();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host stopping: the index is left INVALID and rebuilt on the next start.
        }
        catch (NpgsqlException ex)
        {
            // E.g. another replica building it at the same moment. Search keeps working (sequential scan).
            LogRunSearchIndexFailed(ex);
        }
    }

    /// <summary>No row when the index does not exist; otherwise its <c>pg_index.indisvalid</c>.</summary>
    internal const string RunSearchIndexStateSql = """
        SELECT i.indisvalid FROM pg_index AS i
        JOIN pg_class AS c ON c.oid = i.indexrelid
        WHERE c.relname = 'ix_flow_runs_run_search_trgm' AND c.relnamespace = to_regnamespace(current_schema())
        """;

    /// <summary>The run-search expression index; the expression must stay identical to the query's.</summary>
    internal static readonly string CreateRunSearchIndexSql =
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_flow_runs_run_search_trgm ON flow_runs USING gin (("
        + PostgreSqlRunSearchCommandBuilder.RunSearchExpression(string.Empty) + ") gin_trgm_ops)";

    [LoggerMessage(EventId = 7211, Level = LogLevel.Information, Message = "Building the run-search trigram index concurrently in the background; quick search scans flow_runs until it exists.")]
    private partial void LogBuildingRunSearchIndex();

    [LoggerMessage(EventId = 7212, Level = LogLevel.Information, Message = "Run-search trigram index built.")]
    private partial void LogRunSearchIndexBuilt();

    [LoggerMessage(EventId = 7213, Level = LogLevel.Warning, Message = "Run-search trigram index build failed; quick search keeps working as a scan and the next start retries.")]
    private partial void LogRunSearchIndexFailed(Exception exception);

    [LoggerMessage(EventId = 7214, Level = LogLevel.Warning, Message = "UseRunSearchIndex() is not implemented on PostgreSQL and is ignored: deep search uses the pg_trgm indexes on flow_steps.")]
    private partial void LogTokenIndexNotSupported();

    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS flow_definitions (
            id            UUID         NOT NULL PRIMARY KEY,
            name          VARCHAR(256) NOT NULL,
            version       VARCHAR(64)  NOT NULL DEFAULT '1.0',
            manifest_json TEXT         NULL,
            is_enabled    BOOLEAN      NOT NULL DEFAULT TRUE,
            created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS flow_runs (
            id                UUID         NOT NULL PRIMARY KEY,
            flow_id           UUID         NOT NULL,
            flow_name         VARCHAR(256) NULL,
            status            VARCHAR(64)  NOT NULL,
            trigger_key       VARCHAR(256) NULL,
            trigger_data_json TEXT         NULL,
            background_job_id VARCHAR(128) NULL,
            started_at        TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            completed_at      TIMESTAMPTZ  NULL
        );

        CREATE TABLE IF NOT EXISTS flow_steps (
            run_id        UUID         NOT NULL,
            step_key      VARCHAR(256) NOT NULL,
            step_type     VARCHAR(256) NOT NULL,
            status        VARCHAR(64)  NOT NULL,
            input_json    TEXT         NULL,
            output_json   TEXT         NULL,
            error_message TEXT         NULL,
            job_id        VARCHAR(128) NULL,
            started_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            completed_at  TIMESTAMPTZ  NULL,
            PRIMARY KEY (run_id, step_key)
        );

        CREATE TABLE IF NOT EXISTS flow_step_attempts (
            run_id        UUID         NOT NULL,
            step_key      VARCHAR(256) NOT NULL,
            attempt_no    INT          NOT NULL,
            step_type     VARCHAR(256) NOT NULL,
            status        VARCHAR(64)  NOT NULL,
            input_json    TEXT         NULL,
            output_json   TEXT         NULL,
            error_message TEXT         NULL,
            job_id        VARCHAR(128) NULL,
            started_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            completed_at  TIMESTAMPTZ  NULL,
            PRIMARY KEY (run_id, step_key, attempt_no)
        );

        CREATE TABLE IF NOT EXISTS flow_outputs (
            run_id     UUID         NOT NULL,
            key        VARCHAR(256) NOT NULL,
            value_json TEXT         NULL,
            created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            PRIMARY KEY (run_id, key)
        );

        CREATE TABLE IF NOT EXISTS flow_step_claims (
            run_id     UUID         NOT NULL,
            step_key   VARCHAR(256) NOT NULL,
            claimed_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            PRIMARY KEY (run_id, step_key)
        );

        CREATE TABLE IF NOT EXISTS flow_run_controls (
            run_id                  UUID         NOT NULL PRIMARY KEY,
            flow_id                 UUID         NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
            trigger_key             VARCHAR(256) NOT NULL DEFAULT '',
            idempotency_key         VARCHAR(256) NULL,
            timeout_at_utc          TIMESTAMPTZ  NULL,
            cancel_requested        BOOLEAN      NOT NULL DEFAULT FALSE,
            cancel_reason           TEXT         NULL,
            cancel_requested_at_utc TIMESTAMPTZ  NULL,
            timed_out_at_utc        TIMESTAMPTZ  NULL
        );

        CREATE TABLE IF NOT EXISTS flow_idempotency_keys (
            flow_id          UUID         NOT NULL,
            trigger_key      VARCHAR(256) NOT NULL,
            idempotency_key  VARCHAR(256) NOT NULL,
            run_id           UUID         NOT NULL,
            created_at_utc   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            PRIMARY KEY (flow_id, trigger_key, idempotency_key)
        );

        CREATE INDEX IF NOT EXISTS ix_flow_idempotency_keys_run_id ON flow_idempotency_keys (run_id);

        CREATE TABLE IF NOT EXISTS flow_step_dispatches (
            run_id          UUID         NOT NULL,
            step_key        VARCHAR(256) NOT NULL,
            dispatched_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            dispatch_job_id VARCHAR(128) NULL,
            PRIMARY KEY (run_id, step_key)
        );

        CREATE TABLE IF NOT EXISTS flow_signal_waiters (
            run_id        UUID         NOT NULL,
            step_key      VARCHAR(256) NOT NULL,
            signal_name   VARCHAR(256) NOT NULL,
            created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            expires_at    TIMESTAMPTZ  NULL,
            delivered_at  TIMESTAMPTZ  NULL,
            payload_json  TEXT         NULL,
            PRIMARY KEY (run_id, step_key)
        );

        CREATE INDEX IF NOT EXISTS ix_flow_signal_waiters_run_id_signal_name
            ON flow_signal_waiters (run_id, signal_name);

        CREATE TABLE IF NOT EXISTS flow_schedule_states (
            job_id         VARCHAR(256) NOT NULL PRIMARY KEY,
            flow_id        UUID         NOT NULL,
            flow_name      VARCHAR(256) NOT NULL DEFAULT '',
            trigger_key    VARCHAR(256) NOT NULL,
            is_paused      BOOLEAN      NOT NULL DEFAULT FALSE,
            cron_override  VARCHAR(128) NULL,
            updated_at_utc TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS ix_flow_runs_flow_id ON flow_runs (flow_id);
        CREATE INDEX IF NOT EXISTS ix_flow_runs_status ON flow_runs (status);
        CREATE INDEX IF NOT EXISTS ix_flow_runs_started_at ON flow_runs (started_at DESC);
        CREATE INDEX IF NOT EXISTS ix_flow_runs_flow_id_status_started_at ON flow_runs (flow_id, status, started_at DESC);
        -- INCLUDE mirrors the SQL Server definitions. GetStepStatusesAsync is the engine's single
        -- most frequent query — issued on every step completion and returning every step row in the
        -- run — and without the payload columns in the index Postgres must fetch each row from the
        -- heap. With them the scan is index-only. Postgres has supported INCLUDE since 11.
        CREATE INDEX IF NOT EXISTS ix_flow_steps_run_id_started_at ON flow_steps (run_id, started_at)
            INCLUDE (step_key, step_type, status, job_id, completed_at);
        CREATE INDEX IF NOT EXISTS ix_flow_steps_step_key_run_id ON flow_steps (step_key, run_id);
        CREATE INDEX IF NOT EXISTS ix_flow_step_attempts_run_id_started_at ON flow_step_attempts (run_id, started_at);
        CREATE INDEX IF NOT EXISTS ix_flow_step_attempts_run_id_step_key_attempt_no ON flow_step_attempts (run_id, step_key, attempt_no);

        -- Re-run lineage column (idempotent — Postgres ADD COLUMN IF NOT EXISTS).
        ALTER TABLE flow_runs ADD COLUMN IF NOT EXISTS source_run_id UUID NULL;
        CREATE INDEX IF NOT EXISTS ix_flow_runs_source_run_id ON flow_runs (source_run_id) WHERE source_run_id IS NOT NULL;

        -- When-clause evaluation trace columns (idempotent), parity with the SQL Server backend.
        ALTER TABLE flow_steps ADD COLUMN IF NOT EXISTS evaluation_trace_json TEXT NULL;
        ALTER TABLE flow_step_attempts ADD COLUMN IF NOT EXISTS evaluation_trace_json TEXT NULL;

        -- Webhook hardening tables (v1.25).
        CREATE TABLE IF NOT EXISTS webhook_replay_nonces (
            flow_id     UUID         NOT NULL,
            trigger_key VARCHAR(256) NOT NULL,
            nonce       VARCHAR(256) NOT NULL,
            expires_at  TIMESTAMPTZ  NOT NULL,
            PRIMARY KEY (flow_id, trigger_key, nonce)
        );
        CREATE INDEX IF NOT EXISTS ix_webhook_replay_nonces_expires_at ON webhook_replay_nonces (expires_at);

        CREATE TABLE IF NOT EXISTS webhook_rejections (
            id              BIGSERIAL    PRIMARY KEY,
            flow_id         UUID         NULL,
            trigger_key     VARCHAR(256) NULL,
            received_at     TIMESTAMPTZ  NOT NULL,
            remote_ip       VARCHAR(64)  NULL,
            reason          VARCHAR(64)  NOT NULL,
            status_code     INT          NOT NULL,
            body_bytes      INT          NOT NULL,
            body_truncated  TEXT         NULL,
            headers_json    TEXT         NULL,
            scheme          VARCHAR(128) NULL,
            processing_ms   DOUBLE PRECISION NULL,
            is_accepted     BOOLEAN      NOT NULL DEFAULT FALSE
        );
        CREATE INDEX IF NOT EXISTS ix_webhook_rejections_flow_received ON webhook_rejections (flow_id, received_at DESC);
        CREATE INDEX IF NOT EXISTS ix_webhook_rejections_reason ON webhook_rejections (reason);
        CREATE INDEX IF NOT EXISTS ix_webhook_rejections_received_at ON webhook_rejections (received_at DESC);
        """;

    // Trigram (pg_trgm) GIN indexes for substring run-search acceleration. Run separately
    // from MigrationSql via TryCreateTrigramIndexesAsync because CREATE EXTENSION may be denied
    // on locked-down roles; a failure must not abort the mandatory schema migration.
    private const string TrigramSql = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        CREATE INDEX IF NOT EXISTS ix_flow_runs_flow_name_trgm     ON flow_runs  USING gin (flow_name gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_flow_runs_trigger_key_trgm   ON flow_runs  USING gin (trigger_key gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_flow_steps_step_key_trgm      ON flow_steps USING gin (step_key gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_flow_steps_error_message_trgm ON flow_steps USING gin (error_message gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_flow_steps_output_json_trgm   ON flow_steps USING gin (output_json gin_trgm_ops);
        """;
}
