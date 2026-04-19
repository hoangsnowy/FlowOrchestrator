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
public sealed class PostgreSqlFlowOrchestratorMigrator : IHostedService
{
    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlFlowOrchestratorMigrator> _logger;

    public PostgreSqlFlowOrchestratorMigrator(string connectionString, ILogger<PostgreSqlFlowOrchestratorMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running FlowOrchestrator PostgreSQL migrations...");
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MigrationSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("FlowOrchestrator PostgreSQL migrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlowOrchestrator PostgreSQL migration failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        CREATE TABLE IF NOT EXISTS flow_events (
            sequence      BIGSERIAL    PRIMARY KEY,
            run_id        UUID         NOT NULL,
            timestamp_utc TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            type          VARCHAR(128) NOT NULL,
            step_key      VARCHAR(256) NULL,
            message       TEXT         NULL
        );

        CREATE INDEX IF NOT EXISTS ix_flow_events_run_id_sequence ON flow_events (run_id, sequence);

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
        CREATE INDEX IF NOT EXISTS ix_flow_steps_run_id_started_at ON flow_steps (run_id, started_at);
        CREATE INDEX IF NOT EXISTS ix_flow_steps_step_key_run_id ON flow_steps (step_key, run_id);
        CREATE INDEX IF NOT EXISTS ix_flow_step_attempts_run_id_started_at ON flow_step_attempts (run_id, started_at);
        CREATE INDEX IF NOT EXISTS ix_flow_step_attempts_run_id_step_key_attempt_no ON flow_step_attempts (run_id, step_key, attempt_no);
        """;
}
