using FlowOrchestrator.Testing.Tests.Fixtures;

namespace FlowOrchestrator.Testing.Tests;

/// <summary>Slice 6 — TriggerWebhookAsync routes through the engine and resolves @triggerHeaders().</summary>
public sealed class WebhookTests
{
    [Fact]
    public async Task TriggerWebhookAsync_resolves_triggerHeaders_expression()
    {
        // Arrange
        await using var host = await FlowTestHost.For<WebhookFlow>()
            .WithHandler<CaptureHeaderStepHandler>("CaptureHeader")
            .BuildAsync();

        // Act
        var result = await host.TriggerWebhookAsync(
            slug: "demo-hook",
            body: new { },
            headers: new Dictionary<string, string> { ["X-Foo"] = "bar" },
            timeout: TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("bar", result.Steps["capture"].Output.GetProperty("Captured").GetString());
    }
}
