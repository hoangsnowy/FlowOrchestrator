using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class TriggerMetadataTests
{
    [Fact]
    public void TryGetCronExpression_WithValidCronString_ReturnsTrue()
    {
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron,
            Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
        };

        var found = trigger.TryGetCronExpression(out var cronExpression);

        found.Should().BeTrue();
        cronExpression.Should().Be("*/10 * * * *");
    }

    [Fact]
    public void TryGetCronExpression_WithJsonElementString_ReturnsTrue()
    {
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron,
            Inputs = new Dictionary<string, object?>
            {
                ["cronExpression"] = JsonSerializer.Deserialize<JsonElement>("\"*/5 * * * *\"")
            }
        };

        var found = trigger.TryGetCronExpression(out var cronExpression);

        found.Should().BeTrue();
        cronExpression.Should().Be("*/5 * * * *");
    }

    [Fact]
    public void TryGetCronExpression_WithMissingInput_ReturnsFalse()
    {
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Cron
        };

        var found = trigger.TryGetCronExpression(out _);

        found.Should().BeFalse();
    }

    [Fact]
    public void TryGetCronExpression_ForNonCronTrigger_ReturnsFalse()
    {
        var trigger = new TriggerMetadata
        {
            Type = TriggerType.Manual,
            Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/10 * * * *" }
        };

        var found = trigger.TryGetCronExpression(out _);

        found.Should().BeFalse();
    }
}
