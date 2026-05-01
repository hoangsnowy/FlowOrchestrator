using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowOrchestratorServiceCollectionExtensionsTests
{
    // ─── Fail-fast when no backend is registered ──────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithNoBackend_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddFlowOrchestrator(_ => { });

        // Assert
        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("No FlowOrchestrator storage backend registered", ex.Message);
    }

    // ─── InMemory backend ─────────────────────────────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryFlowStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IFlowStore>();

        // Assert
        Assert.IsType<InMemoryFlowStore>(store);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryFlowRunStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        // Act
        using var provider = services.BuildServiceProvider();
        var runStore = provider.GetRequiredService<IFlowRunStore>();

        // Assert
        Assert.IsType<InMemoryFlowRunStore>(runStore);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_RegistersInMemoryOutputsRepository()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseInMemory());

        // Act
        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IOutputsRepository>();

        // Assert
        Assert.IsType<InMemoryOutputsRepository>(repo);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseInMemory_ExactlyOneOfEachServiceRegistered()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddFlowOrchestrator(b => b.UseInMemory());

        // Assert
        Assert.Single(services, sd => sd.ServiceType == typeof(IFlowStore));
        Assert.Single(services, sd => sd.ServiceType == typeof(IFlowRunStore));
        Assert.Single(services, sd => sd.ServiceType == typeof(IOutputsRepository));
    }

    // ─── SQL Server backend ───────────────────────────────────────────────────

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlFlowStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        // Act
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IFlowStore>();

        // Assert
        Assert.Equal("SqlFlowStore", store.GetType().Name);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlFlowRunStore()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        // Act
        using var provider = services.BuildServiceProvider();
        var runStore = provider.GetRequiredService<IFlowRunStore>();

        // Assert
        Assert.Equal("SqlFlowRunStore", runStore.GetType().Name);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_RegistersSqlOutputsRepository()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        // Act
        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IOutputsRepository>();

        // Assert
        Assert.Equal("SqlOutputsRepository", repo.GetType().Name);
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseSqlServer_DoesNotRegisterInMemoryTypes()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddFlowOrchestrator(b => b.UseSqlServer("Server=.;Database=Test"));

        // Assert
        Assert.Single(services, sd => sd.ServiceType == typeof(IFlowStore));

        var storeDescriptor = services.Single(sd => sd.ServiceType == typeof(IFlowStore));
        if (storeDescriptor.ImplementationType is not null)
        {
            Assert.NotEqual(typeof(InMemoryFlowStore), storeDescriptor.ImplementationType);
        }
    }
}
