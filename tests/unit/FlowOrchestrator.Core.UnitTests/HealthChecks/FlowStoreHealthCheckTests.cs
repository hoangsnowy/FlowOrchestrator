using FlowOrchestrator.Core.HealthChecks;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.HealthChecks;

public class FlowStoreHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenStoreRespondsSuccessfully()
    {
        // Arrange
        var store = Substitute.For<IFlowStore>();
        store.GetAllAsync().Returns(Task.FromResult<IReadOnlyList<FlowDefinitionRecord>>(Array.Empty<FlowDefinitionRecord>()));
        var check = new FlowStoreHealthCheck(store);

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("reachable", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Assert.IsType<int>(result.Data["flow_count"]));
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WithFlowCount_WhenStoreHasDefinitions()
    {
        // Arrange
        var store = Substitute.For<IFlowStore>();
        var records = new[]
        {
            new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "A" },
            new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "B" },
            new FlowDefinitionRecord { Id = Guid.NewGuid(), Name = "C" },
        };
        store.GetAllAsync().Returns(Task.FromResult<IReadOnlyList<FlowDefinitionRecord>>(records));
        var check = new FlowStoreHealthCheck(store);

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(3, Assert.IsType<int>(result.Data["flow_count"]));
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthy_WhenStoreThrows()
    {
        // Arrange
        var store = Substitute.For<IFlowStore>();
        var failure = new InvalidOperationException("connection refused");
        store.GetAllAsync().Returns<Task<IReadOnlyList<FlowDefinitionRecord>>>(_ => throw failure);
        var check = new FlowStoreHealthCheck(store);

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(failure, result.Exception);
        Assert.Contains("unreachable", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthy_WhenProbeTimesOut()
    {
        // Arrange
        var completed = new TaskCompletionSource<IReadOnlyList<FlowDefinitionRecord>>();
        var store = Substitute.For<IFlowStore>();
        // GetAllAsync will await this TCS — never completed by the test, the probe
        // budget is what trips the timeout.
        store.GetAllAsync().Returns(_ => completed.Task);
        var check = new FlowStoreHealthCheck(store, timeout: TimeSpan.FromMilliseconds(50));

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("timed out", result.Description, StringComparison.OrdinalIgnoreCase);

        // Cleanup — release the dangling task so the test runner does not see leaked work.
        completed.TrySetCanceled();
    }

    [Fact]
    public void AddFlowOrchestratorHealthChecks_RegistersTheStorageCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IFlowStore>());

        // Act
        services.AddHealthChecks().AddFlowOrchestratorHealthChecks();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        // Assert
        Assert.Contains(options.Registrations, r => r.Name == "flow-orchestrator-storage");
    }
}
