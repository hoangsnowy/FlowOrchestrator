using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class MetadataInputExtensionsTests
{
    [Fact]
    public void TryGetString_WithJsonElementString_ReturnsTrue()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>
        {
            ["key"] = JsonSerializer.Deserialize<JsonElement>("\"value\"")
        };

        // Act
        var found = inputs.TryGetString("key", out var value);

        // Assert
        Assert.True(found);
        Assert.Equal("value", value);
    }

    [Fact]
    public void TryGetInt32_WithStringValue_ReturnsTrue()
    {
        // Arrange
        var inputs = new Dictionary<string, object?> { ["retry"] = "3" };

        // Act
        var found = inputs.TryGetInt32("retry", out var value);

        // Assert
        Assert.True(found);
        Assert.Equal(3, value);
    }

    [Fact]
    public void TryGetBoolean_WithJsonElementBool_ReturnsTrue()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>
        {
            ["enabled"] = JsonSerializer.Deserialize<JsonElement>("true")
        };

        // Act
        var found = inputs.TryGetBoolean("enabled", out var value);

        // Assert
        Assert.True(found);
        Assert.True(value);
    }

    [Fact]
    public void TryGetDateTimeOffset_WithRoundTripString_ReturnsTrue()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var inputs = new Dictionary<string, object?>
        {
            ["startedAt"] = now.ToString("O")
        };

        // Act
        var found = inputs.TryGetDateTimeOffset("startedAt", out var parsed);

        // Assert
        Assert.True(found);
        Assert.Equal(now, parsed);
    }
}
