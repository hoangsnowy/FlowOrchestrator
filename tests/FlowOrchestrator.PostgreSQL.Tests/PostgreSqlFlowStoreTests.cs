using FlowOrchestrator.Core.Storage;
using FluentAssertions;

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
        var record = new FlowDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Name = "TestFlow",
            Version = "1.0",
            ManifestJson = """{"steps":[]}""",
            IsEnabled = true
        };

        var saved = await _store.SaveAsync(record);

        saved.Id.Should().Be(record.Id);
        saved.Name.Should().Be("TestFlow");
        saved.ManifestJson.Should().Be("""{"steps":[]}""");
        saved.IsEnabled.Should().BeTrue();
        saved.CreatedAt.Should().NotBe(default);
        saved.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _store.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_updates_existing_record()
    {
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "Original", Version = "1.0" });

        var updated = await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "Updated", Version = "2.0" });

        updated.Name.Should().Be("Updated");
        updated.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task SetEnabledAsync_toggles_IsEnabled()
    {
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "FlowEnable", Version = "1.0", IsEnabled = true });

        var disabled = await _store.SetEnabledAsync(id, false);
        disabled.IsEnabled.Should().BeFalse();

        var enabled = await _store.SetEnabledAsync(id, true);
        enabled.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SetEnabledAsync_throws_for_unknown_id()
    {
        var act = async () => await _store.SetEnabledAsync(Guid.NewGuid(), false);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_removes_record()
    {
        var id = Guid.NewGuid();
        await _store.SaveAsync(new FlowDefinitionRecord { Id = id, Name = "ToDelete", Version = "1.0" });

        await _store.DeleteAsync(id);

        var result = await _store.GetByIdAsync(id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_returns_records_ordered_by_name()
    {
        var prefix = Guid.NewGuid().ToString("N")[..8];
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Zebra", Version = "1.0" });
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Alpha", Version = "1.0" });
        await _store.SaveAsync(new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = $"{prefix}_Mango", Version = "1.0" });

        var all = await _store.GetAllAsync();
        var prefixed = all.Where(r => r.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        prefixed.Should().HaveCount(3);
        prefixed[0].Name.Should().Contain("Alpha");
        prefixed[1].Name.Should().Contain("Mango");
        prefixed[2].Name.Should().Contain("Zebra");
    }
}
