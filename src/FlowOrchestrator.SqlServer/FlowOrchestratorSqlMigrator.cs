using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SqlServer;

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
        """;
}
