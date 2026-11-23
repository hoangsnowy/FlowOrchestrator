using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// The recurring-job id is persisted (Hangfire recurring jobs, schedule-state rows, Service Bus cron
/// messages), so its shape is pinned here.
/// </summary>
public sealed class RecurringTriggerJobIdTests
{
    [Fact]
    public void Create_ProducesThePersistedShape()
    {
        // Arrange
        var flowId = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");

        // Act
        var jobId = RecurringTriggerJobId.Create(flowId, "schedule");

        // Assert — identical to the 1.x $"flow-{flowId}-{triggerKey}" interpolation.
        Assert.Equal($"flow-{flowId}-schedule", jobId);
        Assert.Equal("flow-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0-schedule", jobId);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("nightly-run")]
    [InlineData("a")]
    public void TryParse_RoundTripsCreate(string triggerKey)
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, triggerKey);

        // Act
        var parsed = RecurringTriggerJobId.TryParse(jobId, out var parsedFlowId, out var parsedKey);

        // Assert
        Assert.True(parsed);
        Assert.Equal(flowId, parsedFlowId);
        Assert.Equal(triggerKey, parsedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("flow-")]
    [InlineData("job-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0-schedule")]
    [InlineData("flow-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0-")]
    [InlineData("flow-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0")]
    [InlineData("flow-not-a-guid-at-all-xxxxxxxxxxxxxxxxxxx-schedule")]
    [InlineData("flow-0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0_schedule")]
    public void TryParse_MalformedId_ReturnsFalse(string? jobId)
    {
        // Arrange — nothing beyond the inline data.

        // Act
        var parsed = RecurringTriggerJobId.TryParse(jobId, out var flowId, out var triggerKey);

        // Assert
        Assert.False(parsed);
        Assert.Equal(Guid.Empty, flowId);
        Assert.Equal(string.Empty, triggerKey);
    }
}
