using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests;

// UseHangfire() installs a process-wide Hangfire job filter; share the serialized collection with
// the tests that assert on GlobalJobFilters.
[Collection(nameof(Telemetry.HangfireGlobalFiltersCollection))]
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
    public void UseHangfire_RegistersHangfireRuntimeAdapters()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);

        // Act
        builder.UseHangfire();

        // Assert — UseHangfire() is now the extension that wires the runtime, not a flag Core reads.
        Assert.Equal(typeof(HangfireStepDispatcher), ImplementationOf<IStepDispatcher>(services));
        Assert.Equal(typeof(HangfireRecurringTriggerDispatcher), ImplementationOf<IRecurringTriggerDispatcher>(services));
        Assert.Equal(typeof(HangfireRecurringTriggerInspector), ImplementationOf<IRecurringTriggerInspector>(services));
        Assert.Equal(typeof(HangfireFlowOrchestrator), ImplementationOf<IHangfireStepRunner>(services));
        Assert.Equal(typeof(HangfireFlowOrchestrator), ImplementationOf<IHangfireFlowTrigger>(services));
    }

    [Fact]
    public void UseHangfire_AfterAnotherRuntime_KeepsTheFirstRuntimesDispatcher()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new FlowOrchestratorBuilder(services);
        var custom = NSubstitute.Substitute.For<IStepDispatcher>();
        services.AddSingleton(custom);

        // Act
        builder.UseHangfire();

        // Assert — TryAdd semantics: the runtime selected first keeps the dispatcher slot.
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IStepDispatcher));
        Assert.Same(custom, descriptor.ImplementationInstance);
    }

    private static Type? ImplementationOf<TService>(IServiceCollection services)
        => Assert.Single(services, d => d.ServiceType == typeof(TService)).ImplementationType;

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
