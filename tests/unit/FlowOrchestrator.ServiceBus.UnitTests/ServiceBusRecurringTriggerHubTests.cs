using FlowOrchestrator.ServiceBus;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Tests the cron-arithmetic seam of <see cref="ServiceBusRecurringTriggerHub"/>. The full
/// dispatcher needs a live Service Bus connection; the integration suite covers that. Here
/// we verify the pure helper that decides when the next message should fire.
/// </summary>
public class ServiceBusRecurringTriggerHubTests
{
    [Fact]
    public void ComputeNext_FiveFieldCron_ReturnsNextMinute()
    {
        // Arrange
        var baseAt = new DateTimeOffset(2026, 5, 2, 12, 0, 30, TimeSpan.Zero);

        // Act
        var next = ServiceBusRecurringTriggerHub.ComputeNext("* * * * *", baseAt);

        // Assert — Cronos rounds up to the next minute boundary.
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 12, 1, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void ComputeNext_SixFieldSecondsCron_ReturnsNextSecondsTick()
    {
        // Arrange
        var baseAt = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

        // Act
        var next = ServiceBusRecurringTriggerHub.ComputeNext("*/5 * * * * *", baseAt);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 12, 0, 5, TimeSpan.Zero), next);
    }

    [Fact]
    public void ComputeNext_DailyAt9AmCron_ReturnsTomorrow9am()
    {
        // Arrange
        var baseAt = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero);

        // Act
        var next = ServiceBusRecurringTriggerHub.ComputeNext("0 9 * * *", baseAt);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero), next);
    }
}
