using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.InMemory;
using NSubstitute;

namespace FlowOrchestrator.InMemory.UnitTests;

public class InMemoryFlowRepositoryTests
{
    [Fact]
    public async Task Add_AndGetAllFlows_ReturnsAddedFlows()
    {
        // Arrange
        var repo = new InMemoryFlowRepository();
        var flow1 = Substitute.For<IFlowDefinition>();
        var flow2 = Substitute.For<IFlowDefinition>();

        // Act
        repo.Add(flow1);
        repo.Add(flow2);
        var all = await repo.GetAllFlowsAsync();

        // Assert
        Assert.Equal(2, all.Count());
        Assert.Contains(flow1, all);
        Assert.Contains(flow2, all);
    }

    [Fact]
    public async Task GetAllFlows_Empty_ReturnsEmptyList()
    {
        // Arrange
        var repo = new InMemoryFlowRepository();

        // Act
        var all = await repo.GetAllFlowsAsync();

        // Assert
        Assert.Empty(all);
    }
}
