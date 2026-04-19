using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

public class FlowOrchestratorBuilderTests
{
    [Fact]
    public void UseSqlServer_RegistersSqlFlowStore()
    {
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        builder.UseSqlServer("Server=.;Database=Test");

        var descriptor = services.SingleOrDefault(sd => sd.ServiceType == typeof(IFlowStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationFactory.Should().NotBeNull();
        descriptor.ImplementationFactory!(null!).GetType().Name.Should().Be("SqlFlowStore");
    }

    [Fact]
    public void UseHangfire_SetsFlag()
    {
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        builder.UseHangfire();

        builder.HangfireEnabled.Should().BeTrue();
    }

    [Fact]
    public void AddFlow_RegistersFlowDefinition()
    {
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        builder.AddFlow<TestFlow>();

        var sp = services.BuildServiceProvider();
        var flows = sp.GetServices<IFlowDefinition>();
        flows.Should().ContainSingle().Which.Should().BeOfType<TestFlow>();
    }

    [Fact]
    public void FluentChaining_Works()
    {
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        var result = builder
            .UseSqlServer("Server=.")
            .UseHangfire()
            .AddFlow<TestFlow>();

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void Services_IsExposedCorrectly()
    {
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        builder.Services.Should().BeSameAs(services);
    }

    private class TestFlow : IFlowDefinition
    {
        public Guid Id => Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }
}
