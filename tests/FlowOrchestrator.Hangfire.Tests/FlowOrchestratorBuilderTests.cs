using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowOrchestratorBuilderTests
{
    [Fact]
    public void UseSqlServer_RegistersSqlFlowStore()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        builder.UseSqlServer("Server=.;Database=Test");

        // Assert
        var descriptor = services.SingleOrDefault(sd => sd.ServiceType == typeof(IFlowStore));
        Assert.NotNull(descriptor);
        Assert.NotNull(descriptor!.ImplementationFactory);
        Assert.Equal("SqlFlowStore", descriptor.ImplementationFactory!(null!).GetType().Name);
    }

    [Fact]
    public void UseHangfire_SetsFlag()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        builder.UseHangfire();

        // Assert
        Assert.True(builder.HangfireEnabled);
    }

    [Fact]
    public void AddFlow_RegistersFlowDefinition()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        builder.AddFlow<TestFlow>();

        // Assert
        var sp = services.BuildServiceProvider();
        var flows = sp.GetServices<IFlowDefinition>();
        var flow = Assert.Single(flows);
        Assert.IsType<TestFlow>(flow);
    }

    [Fact]
    public void FluentChaining_Works()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        var result = builder
            .UseSqlServer("Server=.")
            .UseHangfire()
            .AddFlow<TestFlow>();

        // Assert
        Assert.Same(builder, result);
    }

    [Fact]
    public void Services_IsExposedCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        var exposed = builder.Services;

        // Assert
        Assert.Same(services, exposed);
    }

    private class TestFlow : IFlowDefinition
    {
        public Guid Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }
}
