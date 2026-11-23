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

    [Fact]
    public async Task TriggerHeaders_resolve_case_insensitively_in_entry_and_later_steps()
    {
        // Arrange
        await using var host = await FlowTestHost.For<HeaderCasingFlow>()
            .WithHandler<CaptureHeaderStepHandler>("CaptureHeader")
            .BuildAsync();

        // Act
        var result = await host.TriggerWebhookAsync(
            slug: "casing-hook",
            body: new { },
            headers: new Dictionary<string, string> { ["X-Request-Id"] = "req-42" },
            timeout: TimeSpan.FromSeconds(30));

        // Assert — the later step rehydrates headers from the store; it must see the same value.
        Assert.False(result.TimedOut);
        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal("req-42", result.Steps["entry"].Output.GetProperty("Captured").GetString());
        Assert.Equal("req-42", result.Steps["later"].Output.GetProperty("Captured").GetString());
    }
}
