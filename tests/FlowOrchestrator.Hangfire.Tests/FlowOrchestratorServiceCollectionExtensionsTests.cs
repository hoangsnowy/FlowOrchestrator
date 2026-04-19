using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowOrchestratorServiceCollectionExtensionsTests
{
    // ─── Fail-fast when no backend is registered ──────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithNoBackend_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddFlowOrchestrator(_ => { });

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*No FlowOrchestrator storage backend registered*");
    }

    // ─── InMemory backend ─────────────────────────────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryFlowStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFlowStore>().Should().BeOfType<InMemoryFlowStore>();
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryFlowRunStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFlowRunStore>().Should().BeOfType<InMemoryFlowRunStore>();
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOutputsRepository>().Should().BeOfType<InMemoryOutputsRepository>();
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_ExactlyOneOfEachServiceRegistered()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        services.Where(sd => sd.ServiceType == typeof(IFlowStore)).Should().ContainSingle();
        services.Where(sd => sd.ServiceType == typeof(IFlowRunStore)).Should().ContainSingle();
        services.Where(sd => sd.ServiceType == typeof(IOutputsRepository)).Should().ContainSingle();
    }

    // ─── SQL Server backend ───────────────────────────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlFlowStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFlowStore>().GetType().Name.Should().Be("SqlFlowStore");
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlFlowRunStore()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IFlowRunStore>().GetType().Name.Should().Be("SqlFlowRunStore");
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlOutputsRepository()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOutputsRepository>().GetType().Name.Should().Be("SqlOutputsRepository");
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_DoesNotRegisterInMemoryTypes()
    {
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        services.Where(sd => sd.ServiceType == typeof(IFlowStore)).Should().ContainSingle();

        var storeDescriptor = services.Single(sd => sd.ServiceType == typeof(IFlowStore));
        if (storeDescriptor.ImplementationType is not null)
            storeDescriptor.ImplementationType.Should().NotBe(typeof(InMemoryFlowStore));
    }
}
