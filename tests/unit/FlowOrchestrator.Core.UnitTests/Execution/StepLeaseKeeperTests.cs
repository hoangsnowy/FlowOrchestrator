using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Hosting;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>Configuration and bookkeeping of <see cref="StepLeaseKeeper"/>, and its registration by <c>AddFlowOrchestrator</c>.</summary>
public sealed class StepLeaseKeeperTests
{
    [Theory]
    [InlineData(60, 20, 20)]     // defaults
    [InlineData(30, 20, 10)]     // capped at a third of the lease
    [InlineData(60, 0, 20)]      // non-positive → default
    public void RenewInterval_is_the_configured_value_capped_at_a_third_of_the_lease(int leaseSeconds, int renewSeconds, int expectedSeconds)
    {
        // Arrange
        var options = new FlowRunControlOptions
        {
            StepLeaseDuration = TimeSpan.FromSeconds(leaseSeconds),
            StepLeaseRenewInterval = TimeSpan.FromSeconds(renewSeconds),
        };

        // Act
        var keeper = new StepLeaseKeeper(options);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), keeper.RenewInterval);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_missing_or_non_positive_duration_turns_leases_off(int? leaseSeconds)
    {
        // Arrange
        var options = new FlowRunControlOptions { StepLeaseDuration = leaseSeconds is { } s ? TimeSpan.FromSeconds(s) : null };

        // Act
        var keeper = new StepLeaseKeeper(options);

        // Assert
        Assert.Null(keeper.Duration);
    }

    [Fact]
    public async Task RenewAsync_renews_only_tracked_leases_and_stops_after_dispose()
    {
        // Arrange
        var store = Substitute.For<IFlowRunRuntimeStore>();
        store.RenewStepLeasesAsync(Arg.Any<IReadOnlyList<StepLease>>(), Arg.Any<TimeSpan>())
            .Returns(call => Task.FromResult<IReadOnlyList<StepLease>>(call.Arg<IReadOnlyList<StepLease>>().ToList()));
        var keeper = new StepLeaseKeeper(new FlowRunControlOptions());
        var lease = new StepLease(Guid.NewGuid(), "work", 3);
        var handle = keeper.Track(lease);

        // Act
        var whileTracked = await keeper.RenewAsync(store);
        handle.Dispose();
        var afterDispose = await keeper.RenewAsync(store);

        // Assert
        Assert.Equal(1, whileTracked);
        Assert.Equal(0, afterDispose);
        Assert.False(handle.IsLost);
        await store.Received(1).RenewStepLeasesAsync(
            Arg.Is<IReadOnlyList<StepLease>>(l => l.Count == 1 && l[0] == lease), FlowRunControlOptions.DefaultStepLeaseDuration);
    }

    [Fact]
    public async Task RenewAsync_marks_a_lease_the_store_no_longer_recognises_as_lost()
    {
        // Arrange
        var store = Substitute.For<IFlowRunRuntimeStore>();
        store.RenewStepLeasesAsync(Arg.Any<IReadOnlyList<StepLease>>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<IReadOnlyList<StepLease>>([]));
        var keeper = new StepLeaseKeeper(new FlowRunControlOptions());
        using var handle = keeper.Track(new StepLease(Guid.NewGuid(), "work", 1));

        // Act
        await keeper.RenewAsync(store);

        // Assert
        Assert.True(handle.IsLost);
    }

    [Fact]
    public async Task RenewAsync_on_a_store_failure_keeps_every_lease_as_it_was()
    {
        // Arrange
        var store = Substitute.For<IFlowRunRuntimeStore>();
        store.RenewStepLeasesAsync(Arg.Any<IReadOnlyList<StepLease>>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromException<IReadOnlyList<StepLease>>(new InvalidOperationException("store down")));
        var keeper = new StepLeaseKeeper(new FlowRunControlOptions());
        using var handle = keeper.Track(new StepLease(Guid.NewGuid(), "work", 1));

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => keeper.RenewAsync(store));

        // Assert
        Assert.Equal("store down", thrown.Message);
        Assert.False(handle.IsLost);
        Assert.Equal(1, keeper.HeldCount);
    }

    [Fact]
    public void AddFlowOrchestrator_registers_the_keeper_and_its_hosted_service()
    {
        // Arrange
        var services = new ServiceCollection().AddLogging();

        // Act
        services.AddFlowOrchestrator(options =>
        {
            options.UseInMemory();
            options.UseInMemoryRuntime();
        });
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Equal(FlowRunControlOptions.DefaultStepLeaseDuration, provider.GetRequiredService<StepLeaseKeeper>().Duration);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is FlowStepLeaseHostedService);
    }
}
