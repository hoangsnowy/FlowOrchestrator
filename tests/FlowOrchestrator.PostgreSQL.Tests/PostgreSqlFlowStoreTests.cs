using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.PostgreSQL.Tests;

public sealed class PostgreSqlFlowStoreTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFlowStore _store;

    public PostgreSqlFlowStoreTests(PostgreSqlFixture fixture)
    {
        _store = new PostgreSqlFlowStore(fixture.ConnectionString);
    }

    [Fact]
    public async Task SaveAsync_then_GetByIdAsync_round_trip()
    {
        // Arrange
        var record = new FlowDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Name = "TestFlow",
            Version = "1.0",
            ManifestJson = """{"steps":[]}""",
            IsEnabled = true
        };

        // Act
        var saved = await _store.SaveAsync(record);

        // Assert
        Assert.Equal(record.Id, saved.Id);
        Assert.Equal("TestFlow", saved.Name);
        Assert.Equal("""{"steps":[]}""", saved.ManifestJson);
        Assert.True(saved.IsEnabled);
        Assert.NotEqual(default, saved.CreatedAt);
        Assert.NotEqual(default, saved.UpdatedAt);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        // Arrange

        // Act
        var result = await _store.GetByIdAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_updates_existing_record()
    {
        // Arrange
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "Original", Version = "1.0" });

        // Act
        var updated = await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "Updated", Version = "2.0" });

        // Assert
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("2.0", updated.Version);
    }

    [Fact]
    public async Task SetEnabledAsync_toggles_IsEnabled()
    {
        // Arrange
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "FlowEnable", Version = "1.0", IsEnabled = true });

        // Act
        var disabled = await _store.SetEnabledAsync(id, false);
        var enabled = await _store.SetEnabledAsync(id, true);

        // Assert
        Assert.False(disabled.IsEnabled);
        Assert.True(enabled.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_throws_for_unknown_id()
    {
        // Arrange
        var act = async () => await _store.SetEnabledAsync(Guid.NewGuid(), false);

        // Act + Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(act);
    }

    [Fact]
    public async Task DeleteAsync_removes_record()
    {
        // Arrange
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "ToDelete", Version = "1.0" });

        // Act
        await _store.DeleteAsync(id);

        // Assert
        var result = await _store.GetByIdAsync(id);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_returns_records_ordered_by_name()
    {
        // Arrange
        var prefix = Guid.NewGuid().ToString("N")[..8];
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Zebra", Version = "1.0" });
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Alpha", Version = "1.0" });
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Mango", Version = "1.0" });

        // Act
        var all = await _store.GetAllAsync();
        var prefixed = all.Where(r => r.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        // Assert
        Assert.Equal(3, prefixed.Count);
        Assert.Contains("Alpha", prefixed[0].Name);
        Assert.Contains("Mango", prefixed[1].Name);
        Assert.Contains("Zebra", prefixed[2].Name);
    }
}
