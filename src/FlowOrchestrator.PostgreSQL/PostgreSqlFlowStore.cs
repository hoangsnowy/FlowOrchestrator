using Dapper;
using FlowOrchestrator.Core.Storage;
using Npgsql;

namespace FlowOrchestrator.PostgreSQL;

/// <summary>
/// Dapper-based PostgreSQL implementation of <see cref="IFlowStore"/>.
/// Uses explicit SQL queries against the <c>flow_definitions</c> table.
/// </summary>
public sealed class PostgreSqlFlowStore : IFlowStore
{
    private readonly string _connectionString;

    public PostgreSqlFlowStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<FlowDefinitionRecord>> GetAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = await conn.QueryAsync<FlowDefinitionRecord>(
            """
            SELECT id AS "Id", name AS "Name", version AS "Version",
                   manifest_json AS "ManifestJson", is_enabled AS "IsEnabled",
                   created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM flow_definitions
            ORDER BY name
            """);
        return rows.AsList();
    }

    public async Task<FlowDefinitionRecord?> GetByIdAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QuerySingleOrDefaultAsync<FlowDefinitionRecord>(
            """
            SELECT id AS "Id", name AS "Name", version AS "Version",
                   manifest_json AS "ManifestJson", is_enabled AS "IsEnabled",
                   created_at AS "CreatedAt", updated_at AS "UpdatedAt"
            FROM flow_definitions
            WHERE id = @Id
            """,
            new { Id = id });
    }

    public async Task<FlowDefinitionRecord> SaveAsync(FlowDefinitionRecord record)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO flow_definitions (id, name, version, manifest_json, is_enabled, created_at, updated_at)
            VALUES (@Id, @Name, @Version, @ManifestJson, @IsEnabled, NOW(), NOW())
            ON CONFLICT (id) DO UPDATE SET
                name          = EXCLUDED.name,
                version       = EXCLUDED.version,
                manifest_json = EXCLUDED.manifest_json,
                is_enabled    = EXCLUDED.is_enabled,
                updated_at    = NOW()
            """,
            new { record.Id, record.Name, record.Version, record.ManifestJson, record.IsEnabled });

        return (await GetByIdAsync(record.Id))!;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM flow_definitions WHERE id = @Id", new { Id = id });
    }

    public async Task<FlowDefinitionRecord> SetEnabledAsync(Guid id, bool enabled)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE flow_definitions SET is_enabled = @Enabled, updated_at = NOW() WHERE id = @Id",
            new { Id = id, Enabled = enabled });
        return (await GetByIdAsync(id)) ?? throw new KeyNotFoundException($"Flow {id} not found.");
    }
}
