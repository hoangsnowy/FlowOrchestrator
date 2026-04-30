using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class TriggerMetadataTests
{
    [Fact]
    public void TryGetCronExpression_WithValidCronString_ReturnsTrue()
    {
        // Arrange
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron,
            Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
        };

        // Act
        var found = trigger.TryGetCronExpression(out var cronExpression);

        // Assert
        Assert.True(found);
        Assert.Equal("*/10 * * * *", cronExpression);
    }

    [Fact]
    public void TryGetCronExpression_WithJsonElementString_ReturnsTrue()
    {
        // Arrange
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron,
            Inputs = new Dictionary<string, object?>
            {
                ["cronExpression"] = JsonSerializer.Deserialize<JsonElement>("\"*/5 * * * *\"")
            }
        };

        // Act
        var found = trigger.TryGetCronExpression(out var cronExpression);

        // Assert
        Assert.True(found);
        Assert.Equal("*/5 * * * *", cronExpression);
    }

    [Fact]
    public void TryGetCronExpression_WithMissingInput_ReturnsFalse()
    {
        // Arrange
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron
        };

        // Act
        var found = trigger.TryGetCronExpression(out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void TryGetCronExpression_ForNonCronTrigger_ReturnsFalse()
    {
        // Arrange
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Manual,
            Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
        };

        // Act
        var found = trigger.TryGetCronExpression(out _);

        // Assert
        Assert.False(found);
    }
}
