using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

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
        // Arrange
        var record = CreateRecord();
        await _sut.SaveAsync(record);

        // Act
        var result = await _sut.GetByIdAsync(record.Id);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TestFlow", result!.Name);
        Assert.NotEqual(default, result.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_SetsCreatedAtOnFirstSave()
    {
        // Arrange
        var record = CreateRecord();

        // Act
        await _sut.SaveAsync(record);

        // Assert
        Assert.NotEqual(default, record.CreatedAt);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllRecords()
    {
        // Arrange
        await _sut.SaveAsync(CreateRecord());
        await _sut.SaveAsync(CreateRecord());

        // Act
        var all = await _sut.GetAllAsync();

        // Assert
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOrderedByName()
    {
        // Arrange
        var r1 = CreateRecord();
        r1.Name = "Bravo";
        var r2 = CreateRecord();
        r2.Name = "Alpha";

        await _sut.SaveAsync(r1);
        await _sut.SaveAsync(r2);

        // Act
        var all = await _sut.GetAllAsync();

        // Assert
        Assert.Equal("Alpha", all[0].Name);
        Assert.Equal("Bravo", all[1].Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        // Arrange
        var record = CreateRecord();
        await _sut.SaveAsync(record);

        // Act
        await _sut.DeleteAsync(record.Id);

        // Assert
        var result = await _sut.GetByIdAsync(record.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_DoesNotThrow()
    {
        // Arrange

        // Act
        var ex = await Record.ExceptionAsync(() => _sut.DeleteAsync(Guid.NewGuid()));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task SetEnabledAsync_UpdatesIsEnabled()
    {
        // Arrange
        var record = CreateRecord();
        record.IsEnabled = true;
        await _sut.SaveAsync(record);

        // Act
        var result = await _sut.SetEnabledAsync(record.Id, false);

        // Assert
        Assert.False(result.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_NonExistentId_ThrowsKeyNotFound()
    {
        // Arrange
        var act = () => _sut.SetEnabledAsync(Guid.NewGuid(), true);

        // Act + Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(act);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        // Arrange

        // Act
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }
}
