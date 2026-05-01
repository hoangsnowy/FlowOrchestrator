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
public sealed class FlowOrchestratorSqlMigrator : IHostedService
{
    private readonly string _connectionString;
    private readonly ILogger<FlowOrchestratorSqlMigrator> _logger;

    public FlowOrchestratorSqlMigrator(string connectionString, ILogger<FlowOrchestratorSqlMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running FlowOrchestrator SQL migrations...");
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MigrationSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("FlowOrchestrator SQL migrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FlowOrchestrator SQL migration failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FlowEvents')
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

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FlowRuns_SourceRunId' AND object_id = OBJECT_ID(N'[FlowRuns]'))
        BEGIN
            EXEC('CREATE INDEX IX_FlowRuns_SourceRunId ON [FlowRuns]([SourceRunId]) WHERE [SourceRunId] IS NOT NULL');
        END
        """;
}
