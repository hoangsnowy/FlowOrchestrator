namespace FlowOrchestrator.StorageMigration.Schema;

/// <summary>The provider-neutral value type of a column; decides how a value is read, normalized, hashed and written.</summary>
internal enum StorageColumnKind
{
    /// <summary><c>UNIQUEIDENTIFIER</c> / <c>UUID</c>.</summary>
    Guid,

    /// <summary><c>NVARCHAR(n|MAX)</c> / <c>VARCHAR(n)</c> or <c>TEXT</c>.</summary>
    String,

    /// <summary><c>BIT</c> / <c>BOOLEAN</c>.</summary>
    Boolean,

    /// <summary><c>INT</c> / <c>INT</c>.</summary>
    Int32,

    /// <summary><c>BIGINT</c> / <c>BIGINT</c> (or <c>BIGSERIAL</c>).</summary>
    Int64,

    /// <summary><c>FLOAT</c> / <c>DOUBLE PRECISION</c>.</summary>
    Double,

    /// <summary><c>DATETIMEOFFSET</c> (100 ns) / <c>TIMESTAMPTZ</c> (1 µs).</summary>
    DateTimeOffset,
}

/// <summary>One column, with its spelling on each backend.</summary>
/// <param name="SqlName">SQL Server name (PascalCase).</param>
/// <param name="PgName">PostgreSQL name (snake_case).</param>
/// <param name="Kind">Value type.</param>
/// <param name="MaxLength">Declared length of a string column; <see langword="null"/> for <c>NVARCHAR(MAX)</c> / <c>TEXT</c>.</param>
internal sealed record StorageColumn(string SqlName, string PgName, StorageColumnKind Kind, int? MaxLength = null)
{
    /// <summary>The column's name on <paramref name="provider"/>.</summary>
    public string NameOn(FlowStorageProvider provider) => provider == FlowStorageProvider.SqlServer ? SqlName : PgName;
}

/// <summary>One FlowOrchestrator table, with its key and its spelling on each backend.</summary>
internal sealed class StorageTable
{
    public StorageTable(string sqlName, string pgName, StorageColumn[] columns, string[] keyColumns)
    {
        SqlName = sqlName;
        PgName = pgName;
        Columns = columns;
        KeyOrdinals = new int[keyColumns.Length];
        for (var k = 0; k < keyColumns.Length; k++)
        {
            KeyOrdinals[k] = Array.FindIndex(columns, c => c.SqlName == keyColumns[k]);
            if (KeyOrdinals[k] < 0)
                throw new InvalidOperationException($"Key column {keyColumns[k]} is not a column of {sqlName}.");
        }
    }

    /// <summary>SQL Server name; also the logical name used in reports.</summary>
    public string SqlName { get; }

    /// <summary>PostgreSQL name.</summary>
    public string PgName { get; }

    /// <summary>Every copied column, in read/write order.</summary>
    public StorageColumn[] Columns { get; }

    /// <summary>Ordinals (into <see cref="Columns"/>) of the primary key, in key order; the keyset cursor walks it.</summary>
    public int[] KeyOrdinals { get; }

    /// <summary>
    /// Ordinal of a <c>BIGINT IDENTITY</c> / <c>BIGSERIAL</c> column whose values are copied as they are and whose
    /// generator is moved past the copied maximum afterwards; -1 when there is none.
    /// </summary>
    public int IdentityOrdinal { get; init; } = -1;

    /// <summary>
    /// Ordinal of a timestamp column below which rows are dead and not copied (expired replay nonces); -1 when
    /// every row is copied.
    /// </summary>
    public int ExpiryOrdinal { get; init; } = -1;

    /// <summary>Columns present on SQL Server that are deliberately not copied (computed columns).</summary>
    public string[] IgnoredSqlColumns { get; init; } = [];

    /// <summary>The 1.x <c>FlowEvents</c> table: copied only when the source has it, created on the target by hand.</summary>
    public bool IsLegacy { get; init; }

    /// <summary>The table's name on <paramref name="provider"/>.</summary>
    public string NameOn(FlowStorageProvider provider) => provider == FlowStorageProvider.SqlServer ? SqlName : PgName;

    /// <inheritdoc/>
    public override string ToString() => SqlName;
}

/// <summary>
/// Every table the SQL Server and PostgreSQL 2.0 migrators create, mapped column by column. Kept in step with
/// <c>FlowOrchestratorSqlMigrator</c> and <c>PostgreSqlFlowOrchestratorMigrator</c>; the schema check refuses
/// to run against a database with columns missing from this map, so a schema change that forgets this file
/// fails loudly instead of dropping data.
/// </summary>
/// <remarks>
/// Not copied, by design: the SQL Server run-search token index (<c>FlowRunSearchTokens</c>,
/// <c>FlowRunSearchIndexState</c>) — derived data that <c>UseRunSearchIndex()</c> back-fills on the target —
/// and the computed columns <c>FlowRuns.IdText</c> / <c>FlowRuns.RunSearchText</c>.
/// </remarks>
internal static class StorageCatalog
{
    private const int KeyLength = 256;

    private static StorageColumn G(string sql, string pg) => new(sql, pg, StorageColumnKind.Guid);

    private static StorageColumn S(string sql, string pg, int? max) => new(sql, pg, StorageColumnKind.String, max);

    private static StorageColumn T(string sql, string pg) => new(sql, pg, StorageColumnKind.DateTimeOffset);

    private static StorageColumn B(string sql, string pg) => new(sql, pg, StorageColumnKind.Boolean);

    private static StorageColumn I(string sql, string pg) => new(sql, pg, StorageColumnKind.Int32);

    private static StorageColumn L(string sql, string pg) => new(sql, pg, StorageColumnKind.Int64);

    private static StorageColumn D(string sql, string pg) => new(sql, pg, StorageColumnKind.Double);

    /// <summary><c>FlowDefinitions</c> ↔ <c>flow_definitions</c>.</summary>
    public static readonly StorageTable FlowDefinitions = new(
        "FlowDefinitions", "flow_definitions",
        [
            G("Id", "id"), S("Name", "name", 256), S("Version", "version", 64), S("ManifestJson", "manifest_json", null),
            B("IsEnabled", "is_enabled"), T("CreatedAt", "created_at"), T("UpdatedAt", "updated_at"),
        ],
        ["Id"]);

    /// <summary><c>FlowScheduleStates</c> ↔ <c>flow_schedule_states</c>.</summary>
    public static readonly StorageTable FlowScheduleStates = new(
        "FlowScheduleStates", "flow_schedule_states",
        [
            S("JobId", "job_id", KeyLength), G("FlowId", "flow_id"), S("FlowName", "flow_name", 256), S("TriggerKey", "trigger_key", 256),
            B("IsPaused", "is_paused"), S("CronOverride", "cron_override", 128), T("UpdatedAtUtc", "updated_at_utc"),
        ],
        ["JobId"]);

    /// <summary><c>FlowRuns</c> ↔ <c>flow_runs</c>.</summary>
    public static readonly StorageTable FlowRuns = new(
        "FlowRuns", "flow_runs",
        [
            G("Id", "id"), G("FlowId", "flow_id"), S("FlowName", "flow_name", 256), S("Status", "status", 64),
            S("TriggerKey", "trigger_key", 256), S("TriggerDataJson", "trigger_data_json", null),
            S("BackgroundJobId", "background_job_id", 128), T("StartedAt", "started_at"), T("CompletedAt", "completed_at"),
            G("SourceRunId", "source_run_id"),
        ],
        ["Id"])
    {
        IgnoredSqlColumns = ["IdText", "RunSearchText"],
    };

    /// <summary><c>FlowRunControls</c> ↔ <c>flow_run_controls</c>.</summary>
    public static readonly StorageTable FlowRunControls = new(
        "FlowRunControls", "flow_run_controls",
        [
            G("RunId", "run_id"), G("FlowId", "flow_id"), S("TriggerKey", "trigger_key", 256), S("IdempotencyKey", "idempotency_key", 256),
            T("TimeoutAtUtc", "timeout_at_utc"), B("CancelRequested", "cancel_requested"), S("CancelReason", "cancel_reason", null),
            T("CancelRequestedAtUtc", "cancel_requested_at_utc"), T("TimedOutAtUtc", "timed_out_at_utc"),
        ],
        ["RunId"]);

    /// <summary><c>FlowIdempotencyKeys</c> ↔ <c>flow_idempotency_keys</c>.</summary>
    public static readonly StorageTable FlowIdempotencyKeys = new(
        "FlowIdempotencyKeys", "flow_idempotency_keys",
        [
            G("FlowId", "flow_id"), S("TriggerKey", "trigger_key", 256), S("IdempotencyKey", "idempotency_key", 256),
            G("RunId", "run_id"), T("CreatedAtUtc", "created_at_utc"),
        ],
        ["FlowId", "TriggerKey", "IdempotencyKey"]);

    /// <summary><c>FlowSteps</c> ↔ <c>flow_steps</c>.</summary>
    public static readonly StorageTable FlowSteps = new(
        "FlowSteps", "flow_steps",
        [
            G("RunId", "run_id"), S("StepKey", "step_key", KeyLength), S("StepType", "step_type", 256), S("Status", "status", 64),
            S("InputJson", "input_json", null), S("OutputJson", "output_json", null), S("ErrorMessage", "error_message", null),
            S("JobId", "job_id", 128), T("StartedAt", "started_at"), T("CompletedAt", "completed_at"),
            S("EvaluationTraceJson", "evaluation_trace_json", null),
        ],
        ["RunId", "StepKey"]);

    /// <summary><c>FlowStepAttempts</c> ↔ <c>flow_step_attempts</c>.</summary>
    public static readonly StorageTable FlowStepAttempts = new(
        "FlowStepAttempts", "flow_step_attempts",
        [
            G("RunId", "run_id"), S("StepKey", "step_key", KeyLength), I("AttemptNo", "attempt_no"), S("StepType", "step_type", 256),
            S("Status", "status", 64), S("InputJson", "input_json", null), S("OutputJson", "output_json", null),
            S("ErrorMessage", "error_message", null), S("JobId", "job_id", 128), T("StartedAt", "started_at"),
            T("CompletedAt", "completed_at"), S("EvaluationTraceJson", "evaluation_trace_json", null),
        ],
        ["RunId", "StepKey", "AttemptNo"]);

    /// <summary><c>FlowOutputs</c> ↔ <c>flow_outputs</c> (trigger data / headers, step inputs and outputs).</summary>
    public static readonly StorageTable FlowOutputs = new(
        "FlowOutputs", "flow_outputs",
        [G("RunId", "run_id"), S("Key", "key", KeyLength), S("ValueJson", "value_json", null), T("CreatedAt", "created_at")],
        ["RunId", "Key"]);

    /// <summary><c>FlowStepDispatches</c> ↔ <c>flow_step_dispatches</c> (dispatch ledger).</summary>
    public static readonly StorageTable FlowStepDispatches = new(
        "FlowStepDispatches", "flow_step_dispatches",
        [G("RunId", "run_id"), S("StepKey", "step_key", KeyLength), T("DispatchedAt", "dispatched_at"), S("DispatchJobId", "dispatch_job_id", 128)],
        ["RunId", "StepKey"]);

    /// <summary><c>FlowStepClaims</c> ↔ <c>flow_step_claims</c> (execute-once claims).</summary>
    public static readonly StorageTable FlowStepClaims = new(
        "FlowStepClaims", "flow_step_claims",
        [G("RunId", "run_id"), S("StepKey", "step_key", KeyLength), T("ClaimedAt", "claimed_at")],
        ["RunId", "StepKey"]);

    /// <summary><c>FlowSignalWaiters</c> ↔ <c>flow_signal_waiters</c>.</summary>
    public static readonly StorageTable FlowSignalWaiters = new(
        "FlowSignalWaiters", "flow_signal_waiters",
        [
            G("RunId", "run_id"), S("StepKey", "step_key", KeyLength), S("SignalName", "signal_name", 256), T("CreatedAt", "created_at"),
            T("ExpiresAt", "expires_at"), T("DeliveredAt", "delivered_at"), S("PayloadJson", "payload_json", null),
        ],
        ["RunId", "StepKey"]);

    /// <summary><c>WebhookReplayNonces</c> ↔ <c>webhook_replay_nonces</c>; expired nonces are not copied.</summary>
    public static readonly StorageTable WebhookReplayNonces = new(
        "WebhookReplayNonces", "webhook_replay_nonces",
        [G("FlowId", "flow_id"), S("TriggerKey", "trigger_key", 256), S("Nonce", "nonce", 256), T("ExpiresAt", "expires_at"), G("RegistrationId", "registration_id")],
        ["FlowId", "TriggerKey", "Nonce"])
    {
        ExpiryOrdinal = 3,
    };

    /// <summary><c>WebhookRejections</c> ↔ <c>webhook_rejections</c> (delivery / rejection log).</summary>
    public static readonly StorageTable WebhookRejections = new(
        "WebhookRejections", "webhook_rejections",
        [
            L("Id", "id"), G("FlowId", "flow_id"), S("TriggerKey", "trigger_key", 256), T("ReceivedAt", "received_at"),
            S("RemoteIp", "remote_ip", 64), S("Reason", "reason", 64), I("StatusCode", "status_code"), I("BodyBytes", "body_bytes"),
            S("BodyTruncated", "body_truncated", null), S("HeadersJson", "headers_json", null), S("Scheme", "scheme", 128),
            D("ProcessingMs", "processing_ms"), B("IsAccepted", "is_accepted"),
        ],
        ["Id"])
    {
        IdentityOrdinal = 0,
    };

    /// <summary>The 1.x run-event log (<c>FlowEvents</c> ↔ <c>flow_events</c>), which 2.0 neither creates nor reads.</summary>
    public static readonly StorageTable LegacyFlowEvents = new(
        "FlowEvents", "flow_events",
        [
            L("Sequence", "sequence"), G("RunId", "run_id"), T("Timestamp", "timestamp_utc"), S("Type", "type", 128),
            S("StepKey", "step_key", 256), S("Message", "message", null),
        ],
        ["Sequence"])
    {
        IdentityOrdinal = 0,
        IsLegacy = true,
    };

    /// <summary>The tables a 2.0 migrator creates, in copy order (definitions first, logs last).</summary>
    public static readonly StorageTable[] CoreTables =
    [
        FlowDefinitions, FlowScheduleStates, FlowRuns, FlowRunControls, FlowIdempotencyKeys, FlowSteps, FlowStepAttempts,
        FlowOutputs, FlowStepDispatches, FlowStepClaims, FlowSignalWaiters, WebhookReplayNonces, WebhookRejections,
    ];

    /// <summary>SQL Server tables that belong to FlowOrchestrator but are rebuilt, not copied.</summary>
    public static readonly string[] SqlDerivedTables = ["FlowRunSearchTokens", "FlowRunSearchIndexState"];

    /// <summary>The 1.34 <c>FlowEvents</c> DDL, issued on a SQL Server target when the source has the legacy table.</summary>
    public const string SqlLegacyEventsDdl = """
        IF OBJECT_ID(N'FlowEvents') IS NULL
        BEGIN
            CREATE TABLE [FlowEvents] (
                [Sequence]   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [RunId]      UNIQUEIDENTIFIER NOT NULL,
                [Timestamp]  DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET(),
                [Type]       NVARCHAR(128)    NOT NULL,
                [StepKey]    NVARCHAR(256)    NULL,
                [Message]    NVARCHAR(MAX)    NULL
            );

            CREATE INDEX IX_FlowEvents_RunId_Sequence ON [FlowEvents]([RunId], [Sequence]);
        END
        """;

    /// <summary>The 1.34 <c>flow_events</c> DDL, issued on a PostgreSQL target when the source has the legacy table.</summary>
    public const string PgLegacyEventsDdl = """
        CREATE TABLE IF NOT EXISTS flow_events (
            sequence      BIGSERIAL    PRIMARY KEY,
            run_id        UUID         NOT NULL,
            timestamp_utc TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            type          VARCHAR(128) NOT NULL,
            step_key      VARCHAR(256) NULL,
            message       TEXT         NULL
        );

        CREATE INDEX IF NOT EXISTS ix_flow_events_run_id_sequence ON flow_events (run_id, sequence);
        """;
}
