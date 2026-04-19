using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FluentAssertions;

namespace FlowOrchestrator.InMemory.Tests;

public class InMemoryFlowStoreTests
{
    private readonly InMemoryFlowStore _sut = new();

    private static FlowDefinitionRecord CreateRecord(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "TestFlow",
        Version = "1.0",
        ManifestJson = "{}",
        IsEnabled = true
    };

    [Fact]
    public async Task SaveAsync_AndGetByIdAsync_ReturnsRecord()
    {
        var record = CreateRecord();
        await _sut.SaveAsync(record);

        var result = await _sut.GetByIdAsync(record.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("TestFlow");
        result.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task SaveAsync_SetsCreatedAtOnFirstSave()
    {
        var record = CreateRecord();

        await _sut.SaveAsync(record);

        record.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllRecords()
    {
        await _sut.SaveAsync(CreateRecord());
        await _sut.SaveAsync(CreateRecord());

        var all = await _sut.GetAllAsync();

        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOrderedByName()
    {
        var r1 = CreateRecord();
        r1.Name = "Bravo";
        var r2 = CreateRecord();
        r2.Name = "Alpha";

        await _sut.SaveAsync(r1);
        await _sut.SaveAsync(r2);

        var all = await _sut.GetAllAsync();
        all[0].Name.Should().Be("Alpha");
        all[1].Name.Should().Be("Bravo");
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        var record = CreateRecord();
        await _sut.SaveAsync(record);

        await _sut.DeleteAsync(record.Id);

        var result = await _sut.GetByIdAsync(record.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        var act = () => _sut.DeleteAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetEnabledAsync_UpdatesIsEnabled()
    {
        var record = CreateRecord();
        record.IsEnabled = true;
        await _sut.SaveAsync(record);

        var result = await _sut.SetEnabledAsync(record.Id, false);

        result.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetEnabledAsync_NonExistentId_ThrowsKeyNotFound()
    {
        var act = () => _sut.SetEnabledAsync(Guid.NewGuid(), true);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }
}
