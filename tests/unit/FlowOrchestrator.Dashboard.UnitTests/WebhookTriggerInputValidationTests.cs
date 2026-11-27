using FlowOrchestrator.Core.Abstractions;
using NSubstitute;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// <c>webhookReplayToleranceSeconds</c> is read as an <see cref="int"/>. A value outside that range used to
/// be truncated — <c>4294967296</c> became <c>0</c>, silently disabling replay protection — so it is now
/// rejected at startup, and clamped (never truncated) for any flow the startup check did not see.
/// </summary>
public sealed class WebhookTriggerInputValidationTests
{
    private static FlowRegistry Registry(object? tolerance)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["webhook"] = new TriggerMetadata
                {
                    Type = TriggerType.Webhook,
                    Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "s", ["webhookReplayToleranceSeconds"] = tolerance }
                }
            },
            Steps = new StepCollection { ["s"] = new StepMetadata { Type = "Noop" } }
        });
        return new FlowRegistry(new[] { flow });
    }

    [Theory]
    [InlineData(4_294_967_296L)]
    [InlineData(-1L)]
    [InlineData("4294967296")]
    public void Out_of_range_replay_tolerance_is_rejected_at_startup(object tolerance)
    {
        // Arrange
        var registry = Registry(tolerance);

        // Act
        var ex = Assert.Throws<InvalidOperationException>(() => WebhookSecurityPipeline.ValidateTriggerInputs(registry));

        // Assert
        Assert.Contains("webhookReplayToleranceSeconds", ex.Message);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(300L)]
    [InlineData("300")]
    [InlineData(0)]
    [InlineData(null)]
    public void In_range_replay_tolerance_is_accepted(object? tolerance)
    {
        // Arrange
        var registry = Registry(tolerance);

        // Act
        var ex = Record.Exception(() => WebhookSecurityPipeline.ValidateTriggerInputs(registry));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task Out_of_range_long_tolerance_clamps_instead_of_disabling_replay_protection()
    {
        // Arrange — unsigned delivery with no timestamp header: rejected only while replay protection is on.
        var pipeline = new WebhookSecurityPipeline(
            new HmacSignatureVerifier(),
            new WebhookSecurityOptions { EnforcementMode = WebhookEnforcementMode.Enforce },
            replayGate: new ReplayProtectionGate(new InMemoryWebhookReplayStore(), TimeProvider.System));
        var inputs = new Dictionary<string, object?> { ["webhookReplayToleranceSeconds"] = 4_294_967_296L };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var verdict = await pipeline.EvaluateAsync(new DefaultHttpContext(), Guid.NewGuid(), "webhook", inputs, "{}"u8.ToArray(), headers);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Reject, verdict.Decision);
        Assert.Equal("missing_timestamp", verdict.Reason);
    }
}
