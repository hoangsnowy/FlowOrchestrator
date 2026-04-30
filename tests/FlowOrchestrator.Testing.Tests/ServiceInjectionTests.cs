using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 8 — handlers can resolve services registered via WithService&lt;TService&gt;().</summary>
public sealed class ServiceInjectionTests
{
    [Fact]
    public async Task WithService_makes_fake_resolvable_to_handler_ctor()
    {
        // Arrange
        IGreeter fake = new FakeGreeter();
        await using var host = await FlowTestHost.For<ServiceConsumerFlow>()
            .WithService(fake)
            .WithHandler<GreetStepHandler>("Greet")
            .BuildAsync();

        // Act
        var result = await host.TriggerAsync(timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("hello world", result.Steps["greet"].Output.GetProperty("Message").GetString());
    }
}
