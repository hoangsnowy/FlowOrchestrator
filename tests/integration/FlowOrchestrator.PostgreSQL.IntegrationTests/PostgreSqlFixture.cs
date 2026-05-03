using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace FlowOrchestrator.PostgreSQL.Tests;

/// <summary>
/// Shared Testcontainers fixture: starts one Postgres container per test class
/// and runs migrations once before all tests in the class execute.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await TestcontainerStartHelper.StartWithRetryAsync(_container);
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

/// <summary>
/// Retries <c>StartAsync</c> on transient Docker daemon failures (image-pull
/// races, Ryuk reaper start contention) that surface when xUnit fans tests
/// across multiple TFMs in parallel. Without retry, the first attempt's pull
/// or reaper start can race with a sibling TFM's identical request and either
/// hit a partial-tag fetch error or the reaper container's "already exists"
/// 409. Three attempts with linear backoff has eliminated this in local CI.
/// </summary>
internal static class TestcontainerStartHelper
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
