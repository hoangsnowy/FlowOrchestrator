using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace FlowOrchestrator.StorageMigration.IntegrationTests;

/// <summary>
/// One SQL Server and one PostgreSQL container for the assembly. Tests never share a database: each one asks
/// for fresh, empty databases and the copy creates the target schema itself.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Same image as the SQL Server integration suite (2025 images through CU11 crash intermittently at start).
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private int _counter;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        await StartWithRetryAsync(_pg);
        await StartWithRetryAsync(_sql);
    }

    /// <summary>Creates an empty SQL Server database and returns its connection string.</summary>
    public async Task<string> CreateSqlServerDatabaseAsync()
    {
        var name = "fo_copy_" + Interlocked.Increment(ref _counter) + "_" + Guid.NewGuid().ToString("N")[..8];
        await using (var conn = new SqlConnection(_sql.GetConnectionString()))
            await conn.ExecuteAsync($"CREATE DATABASE [{name}]");
        return new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Creates an empty PostgreSQL database and returns its connection string.</summary>
    public async Task<string> CreatePostgreSqlDatabaseAsync()
    {
        var name = "fo_copy_" + Interlocked.Increment(ref _counter) + "_" + Guid.NewGuid().ToString("N")[..8];
        await using (var conn = new NpgsqlConnection(_pg.GetConnectionString()))
            await conn.ExecuteAsync($"CREATE DATABASE \"{name}\"");
        return new NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>Creates an empty database on <paramref name="provider"/>.</summary>
    public Task<string> CreateDatabaseAsync(FlowStorageProvider provider) =>
        provider == FlowStorageProvider.SqlServer ? CreateSqlServerDatabaseAsync() : CreatePostgreSqlDatabaseAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private static async Task StartWithRetryAsync(IContainer container, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                await container.StartAsync();
                return;
            }
            catch (Exception) when (i < attempts)
            {
                // Transient Docker failures (image-pull races, reaper start contention); see the SQL Server suite.
                await Task.Delay(TimeSpan.FromSeconds(2 * i));
            }
        }
    }
}
