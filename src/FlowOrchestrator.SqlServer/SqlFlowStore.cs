using Dapper;
using FlowOrchestrator.Core.Storage;
using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Dapper-based SQL Server implementation of <see cref="IFlowStore"/>.
/// Uses explicit SQL queries against the <c>FlowDefinitions</c> table.
/// </summary>
public sealed class SqlFlowStore : IFlowStore
{
    private readonly string _connectionString;

    public SqlFlowStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowDefinitionRecord>(
            "SELECT Id, Name, Version, ManifestJson, IsEnabled, CreatedAt, UpdatedAt FROM FlowDefinitions ORDER BY Name");
        return rows.AsList();
    }

    public async Task<FlowDefinitionRecord?> GetByIdAsync(Guid id)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowDefinitionRecord>(
            "SELECT Id, Name, Version, ManifestJson, IsEnabled, CreatedAt, UpdatedAt FROM FlowDefinitions WHERE Id = @Id",
            new { Id = id });
    }

    public async Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record)
    {
        await using var conn = new SqlConnection(_connectionString);
        var existing = await conn.QuerySingleOrDefaultAsync<int>(
            "SELECT 1 FROM FlowDefinitions WHERE Id = @Id", new { record.Id });

        if (existing == 1)
        {
            await conn.ExecuteAsync("""
                UPDATE FlowDefinitions
                SET Name = @Name, Version = @Version, ManifestJson = @ManifestJson,
                    IsEnabled = @IsEnabled, UpdatedAt = SYSDATETIMEOFFSET()
                WHERE Id = @Id
                """, record);
        }
        else
        {
            await conn.ExecuteAsync("""
                INSERT INTO FlowDefinitions (Id, Name, Version, ManifestJson, IsEnabled, CreatedAt, UpdatedAt)
                VALUES (@Id, @Name, @Version, @ManifestJson, @IsEnabled, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
                """, record);
        }

        return (await GetByIdAsync(record.Id))!;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM FlowDefinitions WHERE Id = @Id", new { Id = id });
    }

    public async Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FlowDefinitions SET IsEnabled = @Enabled, UpdatedAt = SYSDATETIMEOFFSET() WHERE Id = @Id",
            new { Id = id, Enabled = enabled });
        return (await GetByIdAsync(id)) ?? throw new KeyNotFoundException($"Flow {id} not found.");
    }
}
