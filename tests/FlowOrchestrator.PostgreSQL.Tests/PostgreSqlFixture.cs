using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Shared Testcontainers fixture: starts one Postgres container per test class
/// and runs migrations once before all tests in the class execute.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var migrator = new PostgreSqlFlowOrchestratorMigrator(
            ConnectionString,
            NullLogger<PostgreSqlFlowOrchestratorMigrator>.Instance);
        await migrator.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
