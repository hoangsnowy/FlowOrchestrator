using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowOrchestratorServiceCollectionExtensionsTests
{
    // ─── IOutputsRepository registration ─────────────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithoutConnectionString_RegistersInMemoryOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(_ => { });

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IOutputsRepository));

        descriptor.ImplementationType.Should().Be(typeof(InMemoryOutputsRepository));
    }

    [Fact]
    public void AddFlowOrchestrator_WithConnectionString_RegistersSqlOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IOutputsRepository));

        // SqlOutputsRepository is internal — verify via factory invocation
        descriptor.ImplementationFactory.Should().NotBeNull("SQL mode must register via factory");
        var instance = descriptor.ImplementationFactory!(null!);
        instance.GetType().Name.Should().Be("SqlOutputsRepository");
    }

    [Fact]
    public void AddFlowOrchestrator_WithConnectionString_DoesNotRegisterInMemoryOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        var descriptors = services.Where(sd => sd.ServiceType == typeof(IOutputsRepository)).ToList();

        descriptors.Should().ContainSingle("exactly one IOutputsRepository must be registered");
        descriptors[0].ImplementationType.Should().NotBe(typeof(InMemoryOutputsRepository));
    }

    [Fact]
    public void AddFlowOrchestrator_WithoutConnectionString_ExactlyOneOutputsRepositoryRegistered()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(_ => { });

        var descriptors = services.Where(sd => sd.ServiceType == typeof(IOutputsRepository)).ToList();

        descriptors.Should().ContainSingle("exactly one IOutputsRepository must be registered");
    }

    // ─── IFlowStore / IFlowRunStore registration ──────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithoutConnectionString_RegistersInMemoryFlowStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(_ => { });

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IFlowStore));

        descriptor.ImplementationType.Should().Be(typeof(InMemoryFlowStore));
    }

    [Fact]
    public void AddFlowOrchestrator_WithoutConnectionString_RegistersInMemoryFlowRunStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(_ => { });

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IFlowRunStore));

        descriptor.ImplementationType.Should().Be(typeof(InMemoryFlowRunStore));
    }

    [Fact]
    public void AddFlowOrchestrator_WithConnectionString_RegistersSqlFlowStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IFlowStore));

        descriptor.ImplementationFactory.Should().NotBeNull();
        var instance = descriptor.ImplementationFactory!(null!);
        instance.GetType().Name.Should().Be("SqlFlowStore");
    }

    [Fact]
    public void AddFlowOrchestrator_WithConnectionString_RegistersSqlFlowRunStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        var descriptor = services.Single(sd => sd.ServiceType == typeof(IFlowRunStore));

        descriptor.ImplementationFactory.Should().NotBeNull();
        var instance = descriptor.ImplementationFactory!(null!);
        instance.GetType().Name.Should().Be("SqlFlowRunStore");
    }
}
