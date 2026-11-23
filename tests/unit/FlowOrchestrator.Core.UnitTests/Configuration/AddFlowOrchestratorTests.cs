using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Configuration;

/// <summary>
/// <c>AddFlowOrchestrator</c> lives in Core since 2.0: this project references no Hangfire assembly,
/// so these tests also prove an in-memory or custom-runtime host wires up without it.
/// </summary>
public sealed class AddFlowOrchestratorTests
{
    private sealed class SampleFlow : IFlowDefinition
    {
        public Guid Id { get; } = Guid.Parse("6f1d5d5e-1c1a-4c55-9a4e-0d2f5f0e7a11");
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new();
    }

    [Fact]
    public void StorageWithoutRuntime_ThrowsNamingTheRuntimePackages()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddFlowOrchestrator(o => o.UseInMemory()));

        // Assert
        Assert.Contains("No FlowOrchestrator runtime registered", ex.Message);
        Assert.Contains("UseHangfire()", ex.Message);
        Assert.Contains("UseInMemoryRuntime()", ex.Message);
    }

    [Fact]
    public void CustomStepDispatcherWithoutRecurringDispatcher_Throws()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.Services.AddSingleton(Substitute.For<IStepDispatcher>());
        }));

        // Assert
        Assert.Contains("IRecurringTriggerDispatcher", ex.Message);
    }

    [Fact]
    public void InMemoryRuntime_ResolvesTheCoreDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.UseInMemoryRuntime();
            o.AddFlow<SampleFlow>();
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<FlowRegistry>();

        // Assert
        var flow = Assert.Single(registry.Flows);
        Assert.IsType<SampleFlow>(flow);
        Assert.Same(flow, registry.Find(flow.Id));
        Assert.Same(registry, sp.GetRequiredService<FlowRegistry>());
        Assert.IsType<RecurringTriggerSync>(sp.GetRequiredService<IRecurringTriggerSync>());
        Assert.IsType<InMemoryFlowScheduleStateStore>(sp.GetRequiredService<IFlowScheduleStateStore>());
        Assert.NotNull(sp.GetRequiredService<IFlowOrchestrator>());
        Assert.Contains(sp.GetServices<IHostedService>(), h => h.GetType().Name == "FlowSyncHostedService");
        Assert.Contains(sp.GetServices<IHostedService>(), h => h.GetType().Name == "FlowRetentionHostedService");
    }

    [Fact]
    public void BackendScheduleStateStore_WinsOverTheInMemoryDefault()
    {
        // Arrange
        var durable = Substitute.For<IFlowScheduleStateStore>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.UseInMemoryRuntime();
            o.Services.AddSingleton(durable);
        });

        // Act
        using var sp = services.BuildServiceProvider();

        // Assert
        Assert.Same(durable, sp.GetRequiredService<IFlowScheduleStateStore>());
        Assert.Single(services, d => d.ServiceType == typeof(IFlowScheduleStateStore));
    }

    [Fact]
    public void CoreAssembly_DoesNotReferenceHangfireOrNewtonsoft()
    {
        // Arrange
        var core = typeof(FlowRegistry).Assembly;

        // Act
        var referenced = core.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        // Assert
        Assert.DoesNotContain(referenced, n => n.StartsWith("Hangfire", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => n.StartsWith("FlowOrchestrator.Hangfire", StringComparison.Ordinal));
        Assert.DoesNotContain("Newtonsoft.Json", referenced);
    }
}
