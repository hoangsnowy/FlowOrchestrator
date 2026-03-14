using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.SampleApp;

/// <summary>
/// Creates and seeds the sample database tables (Orders, ProcessedResults)
/// required by the OrderProcessingFlow demo. Runs once at startup.
/// </summary>
public sealed class SampleDataMigrator : IHostedService
{
    private readonly string _connectionString;
    private readonly ILogger<SampleDataMigrator> _logger;

    public SampleDataMigrator(string connectionString, ILogger<SampleDataMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running SampleApp database migrations...");
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MigrationSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("SampleApp database migrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SampleApp database migration failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private const string MigrationSql = """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Orders')
        BEGIN
            CREATE TABLE [Orders] (
                [Id]           INT              NOT NULL IDENTITY(1,1) PRIMARY KEY,
                [CustomerName] NVARCHAR(256)    NOT NULL,
                [Total]        DECIMAL(18,2)    NOT NULL,
                [Status]       NVARCHAR(64)     NOT NULL DEFAULT 'Pending',
                [CreatedAt]    DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );

            INSERT INTO [Orders] ([CustomerName], [Total], [Status]) VALUES
                ('Alice Johnson',   120.50, 'Pending'),
                ('Bob Smith',       340.00, 'Pending'),
                ('Carol Williams',   89.99, 'Pending'),
                ('David Brown',     210.75, 'Pending'),
                ('Eve Davis',        55.00, 'Pending'),
                ('Frank Miller',    480.25, 'Completed'),
                ('Grace Wilson',    130.00, 'Pending'),
                ('Henry Moore',     295.60, 'Pending'),
                ('Ivy Taylor',       72.40, 'Pending'),
                ('Jack Anderson',   160.00, 'Pending');
        END

        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProcessedResults')
        BEGIN
            CREATE TABLE [ProcessedResults] (
                [Id]        INT              NOT NULL IDENTITY(1,1) PRIMARY KEY,
                [RunId]     UNIQUEIDENTIFIER NOT NULL,
                [Data]      NVARCHAR(MAX)    NULL,
                [CreatedAt] DATETIMEOFFSET   NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );
        END
        """;
}
