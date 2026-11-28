using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.Core.Tests.Configuration;

/// <summary><see cref="FlowServerOptions"/> validation and its registration by <c>AddFlowOrchestrator</c>.</summary>
public sealed class FlowServerOptionsTests
{
    [Fact]
    public void Defaults_are_a_15_second_heartbeat_stale_after_45_seconds_purged_after_an_hour()
    {
        // Arrange
        var options = new FlowServerOptions();

        // Act
        var staleAfter = options.StaleAfter;

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(15), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), staleAfter);
        Assert.Equal(TimeSpan.FromHours(1), options.PurgeAfter);
        Assert.Null(options.RuntimeName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(600_001)]
    public void HeartbeatInterval_outside_one_second_to_ten_minutes_is_rejected(int milliseconds)
    {
        // Arrange
        var options = new FlowServerOptions();

        // Act
        var ex = Record.Exception(() => options.HeartbeatInterval = TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void MissedHeartbeatsBeforeStale_outside_two_to_a_hundred_is_rejected(int missed)
    {
        // Arrange
        var options = new FlowServerOptions();

        // Act
        var ex = Record.Exception(() => options.MissedHeartbeatsBeforeStale = missed);

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void PurgeAfter_must_be_positive()
    {
        // Arrange
        var options = new FlowServerOptions();

        // Act
        var ex = Record.Exception(() => options.PurgeAfter = TimeSpan.Zero);

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void StaleAfter_follows_the_interval_and_the_missed_count()
    {
        // Arrange
        var options = new FlowServerOptions { HeartbeatInterval = TimeSpan.FromSeconds(10), MissedHeartbeatsBeforeStale = 5 };

        // Act
        var staleAfter = options.StaleAfter;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(50), staleAfter);
    }

    [Fact]
    public void AddFlowOrchestrator_rejects_a_purge_age_not_longer_than_the_stale_threshold()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.UseInMemoryRuntime();
            o.Servers.PurgeAfter = TimeSpan.FromSeconds(45);
        }));

        // Assert
        Assert.Contains("PurgeAfter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFlowOrchestrator_registers_the_options_the_heartbeat_service_and_the_in_memory_server_store()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowOrchestrator(o =>
        {
            o.UseInMemory();
            o.UseInMemoryRuntime();
            o.Servers.HeartbeatInterval = TimeSpan.FromSeconds(5);
        });

        // Act
        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<FlowServerOptions>();

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(5), options.HeartbeatInterval);
        Assert.Equal("InMemory", options.RuntimeName);
        Assert.NotNull(sp.GetService<IFlowServerStore>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s.GetType().Name == "FlowServerHeartbeatHostedService");
    }
}
