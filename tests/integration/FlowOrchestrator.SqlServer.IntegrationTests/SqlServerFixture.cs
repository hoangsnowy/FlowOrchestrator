using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var migrator = new FlowOrchestratorSqlMigrator(
            ConnectionString,
            NullLogger<FlowOrchestratorSqlMigrator>.Instance);
        await migrator.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
