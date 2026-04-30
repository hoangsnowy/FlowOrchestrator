using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Invariant #10 — every supported (runtime × storage) combination must build a valid
/// DI container and resolve <see cref="IFlowOrchestrator"/>, <see cref="IStepDispatcher"/>,
/// and <see cref="IRecurringTriggerDispatcher"/> without throwing.
/// </summary>
/// <remarks>
/// Hangfire-runtime combinations are verified in <c>FlowOrchestrator.Hangfire.Tests</c>
/// because <c>HangfireStepDispatcher</c> is internal to that assembly. This file focuses
/// on the InMemory-runtime side, which is the new path introduced by Delta 1.
/// </remarks>
public sealed class RuntimeStorageMatrixTests
{
    [Fact]
    public void InMemoryRuntime_InMemoryStorage_ResolvesAllRuntimeServices()
    {
        // Arrange
        using var sp = BuildInMemoryStack();

        // Act + Assert — none of these should throw.
        Assert.NotNull(sp.GetRequiredService<IFlowOrchestrator>());
        Assert.NotNull(sp.GetRequiredService<IStepDispatcher>());
        Assert.NotNull(sp.GetRequiredService<IRecurringTriggerDispatcher>());
        Assert.NotNull(sp.GetRequiredService<IRecurringTriggerInspector>());
        Assert.NotNull(sp.GetRequiredService<IRecurringTriggerSync>());
    }

    [Fact]
    public void InMemoryRuntime_RegistersInMemoryStepDispatcher()
    {
        // Arrange
        using var sp = BuildInMemoryStack();

        // Act
        var dispatcher = sp.GetRequiredService<IStepDispatcher>();

        // Assert
        Assert.IsType<InMemoryStepDispatcher>(dispatcher);
    }

    [Fact]
    public void InMemoryRuntime_RegistersPeriodicTimerRecurringDispatcher()
    {
        // Arrange
        using var sp = BuildInMemoryStack();

        // Act
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();

        // Assert
        Assert.IsType<PeriodicTimerRecurringTriggerDispatcher>(dispatcher);
    }

    [Fact]
    public void InMemoryRuntime_DoesNotRegisterHangfireStepDispatcher()
    {
        // Arrange
        using var sp = BuildInMemoryStack();

        // Act
        var dispatcher = sp.GetRequiredService<IStepDispatcher>();

        // Assert — InMemory mode must not leak any Hangfire-prefixed runtime types.
        Assert.DoesNotContain("Hangfire", dispatcher.GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public void InMemoryRuntime_RecurringDispatcherInspectorSyncShareInstance()
    {
        // Arrange
        using var sp = BuildInMemoryStack();

        // Act
        var dispatcher = sp.GetRequiredService<IRecurringTriggerDispatcher>();
        var inspector = sp.GetRequiredService<IRecurringTriggerInspector>();
        var sync = sp.GetRequiredService<IRecurringTriggerSync>();

        // Assert — single PeriodicTimer instance fulfils all three roles.
        Assert.Same(dispatcher, inspector);
        Assert.Same(dispatcher, sync);
    }

    private static ServiceProvider BuildInMemoryStack()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(opts =>
        {
            opts.UseInMemory();
            opts.UseInMemoryRuntime();
        });
        return services.BuildServiceProvider();
    }
}
