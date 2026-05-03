using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.Hangfire.Telemetry;
using FlowOrchestrator.InMemory;
using Hangfire;
using Hangfire.Client;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Hangfire.Tests.Telemetry;

/// <summary>Marks tests that touch <see cref="GlobalJobFilters"/> so xUnit serializes them — the global filter list is process-wide mutable state and parallel runs race on it.</summary>
[CollectionDefinition(nameof(HangfireGlobalFiltersCollection), DisableParallelization = true)]
public sealed class HangfireGlobalFiltersCollection
{
}

/// <summary>
/// Unit tests for the registration side of <see cref="TraceContextHangfireFilter"/> — verifying
/// that calling <c>AddFlowOrchestrator(...).UseHangfire()</c> wires the filter into Hangfire's
/// global filter list and that double-registration is idempotent.
/// </summary>
/// <remarks>
/// Direct unit tests of <c>IClientFilter.OnCreating</c> / <c>IServerFilter.OnPerforming</c>
/// are deferred to the integration test suite because constructing Hangfire's
/// <see cref="CreatingContext"/> / <c>PerformingContext</c> requires a live <see cref="JobStorage"/>
/// connection. The filter logic itself is small, single-purpose, and exercised end-to-end by the
/// trace-propagation integration tests.
/// </remarks>
[Collection(nameof(HangfireGlobalFiltersCollection))]
public sealed class TraceContextHangfireFilterTests : IDisposable
{
    public TraceContextHangfireFilterTests()
    {
        // Snapshot the filter list before each test so we can restore it afterwards.
        _initialFilterCount = GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter).Count();
    }

    private readonly int _initialFilterCount;

    public void Dispose()
    {
        // Best-effort cleanup so test runs do not leak state into one another. JobFilterCollection.Remove
        // matches by .Instance reference, so we pass the wrapped filter instance, not the JobFilter wrapper.
        var leaked = GlobalJobFilters.Filters
            .Where(f => f.Instance is TraceContextHangfireFilter)
            .Select(f => f.Instance)
            .ToList();
        for (var i = 0; i < leaked.Count - _initialFilterCount; i++)
        {
            GlobalJobFilters.Filters.Remove(leaked[i]);
        }
    }

    [Fact]
    public void AddFlowOrchestrator_WithUseHangfire_RegistersTraceContextHangfireFilter()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddFlowOrchestrator(b => b.UseInMemory().UseHangfire());

        // Assert
        Assert.Contains(
            GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter),
            _ => true);
    }

    [Fact]
    public void AddFlowOrchestrator_WithoutUseHangfire_DoesNotRegisterFilter()
    {
        // Arrange
        var initialCount = GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter).Count();
        var services = new ServiceCollection();

        // Act
        services.AddFlowOrchestrator(b => b.UseInMemory());

        // Assert
        var afterCount = GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter).Count();
        Assert.Equal(initialCount, afterCount);
    }

    [Fact]
    public void AddFlowOrchestrator_CalledTwice_DoesNotStackFilter()
    {
        // Arrange
        var s1 = new ServiceCollection();
        var s2 = new ServiceCollection();

        // Act
        s1.AddFlowOrchestrator(b => b.UseInMemory().UseHangfire());
        var firstCount = GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter).Count();

        s2.AddFlowOrchestrator(b => b.UseInMemory().UseHangfire());
        var secondCount = GlobalJobFilters.Filters.Where(f => f.Instance is TraceContextHangfireFilter).Count();

        // Assert
        Assert.Equal(firstCount, secondCount);
    }
}
