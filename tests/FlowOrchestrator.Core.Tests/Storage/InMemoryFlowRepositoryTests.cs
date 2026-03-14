using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FluentAssertions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Storage;

public class InMemoryFlowRepositoryTests
{
    [Fact]
    public async Task Add_AndGetAllFlows_ReturnsAddedFlows()
    {
        var repo = new InMemoryFlowRepository();
        var flow1 = Substitute.For<IFlowDefinition>();
        var flow2 = Substitute.For<IFlowDefinition>();

        repo.Add(flow1);
        repo.Add(flow2);

        var all = await repo.GetAllFlowsAsync();

        all.Should().HaveCount(2);
        all.Should().Contain(flow1);
        all.Should().Contain(flow2);
    }

    [Fact]
    public async Task GetAllFlows_Empty_ReturnsEmptyList()
    {
        var repo = new InMemoryFlowRepository();

        var all = await repo.GetAllFlowsAsync();

        all.Should().BeEmpty();
    }
}
