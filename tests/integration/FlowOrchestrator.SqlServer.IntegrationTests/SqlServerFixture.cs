using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace FlowOrchestrator.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await SqlServerStartHelper.StartWithRetryAsync(_container);
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

/// <summary>
/// Retries <c>StartAsync</c> on transient Docker daemon failures
/// (image-pull races, Ryuk reaper start contention) that surface when xUnit
/// fans tests across multiple TFMs in parallel. See
/// <c>PostgreSqlFixture</c> for the same pattern.
/// </summary>
internal static class SqlServerStartHelper
{
    public static async Task StartWithRetryAsync(IContainer container, int attempts = 3, int initialDelayMs = 2000)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                await container.StartAsync();
                return;
            }
            catch (Exception ex) when (i < attempts - 1)
            {
                last = ex;
                await Task.Delay(initialDelayMs * (i + 1));
            }
        }
        if (last is not null) throw last;
    }
}
