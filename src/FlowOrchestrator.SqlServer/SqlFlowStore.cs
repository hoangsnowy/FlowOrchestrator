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
        // Atomic upsert. The prior SELECT-then-INSERT/UPDATE was non-atomic: two concurrent
        // first-saves of the same Id both observed "not exists" and raced into duplicate INSERTs,
        // throwing a PK violation. MERGE with (HOLDLOCK, UPDLOCK) takes a serializable key-range
        // lock in *update* mode on the target key, so the second writer blocks until the first
        // commits and then matches and UPDATEs. UPDLOCK is what prevents the classic
        // concurrent-MERGE deadlock: without it both writers take a range S-lock on the empty
        // gap and then deadlock trying to upgrade to X for the INSERT. (Mirrors the upsert intent
        // of SqlFlowScheduleStateStore.SaveAsync and the ON CONFLICT upsert in the Postgres backend.)
        // CreatedAt is set only on INSERT so the original creation timestamp survives updates.
        await conn.ExecuteAsync("""
            MERGE FlowDefinitions WITH (HOLDLOCK, UPDLOCK) AS target
            USING (SELECT @Id AS Id) AS source
            ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET
                    Name = @Name,
                    Version = @Version,
                    ManifestJson = @ManifestJson,
                    IsEnabled = @IsEnabled,
                    UpdatedAt = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN
                INSERT (Id, Name, Version, ManifestJson, IsEnabled, CreatedAt, UpdatedAt)
                VALUES (@Id, @Name, @Version, @ManifestJson, @IsEnabled, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """, record);

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
