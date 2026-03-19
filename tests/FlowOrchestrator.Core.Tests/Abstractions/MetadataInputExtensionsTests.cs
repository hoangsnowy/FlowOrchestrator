using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class MetadataInputExtensionsTests
{
    [Fact]
    public void TryGetString_WithJsonElementString_ReturnsTrue()
    {
        var inputs = new Dictionary<string, object?>
        {
            ["key"] = JsonSerializer.Deserialize<JsonElement>("\"value\"")
        };

        var found = inputs.TryGetString("key", out var value);

        found.Should().BeTrue();
        value.Should().Be("value");
    }

    [Fact]
    public void TryGetInt32_WithStringValue_ReturnsTrue()
    {
        var inputs = new Dictionary<string, object?> { ["retry"] = "3" };

        var found = inputs.TryGetInt32("retry", out var value);

        found.Should().BeTrue();
        value.Should().Be(3);
    }

    [Fact]
    public void TryGetBoolean_WithJsonElementBool_ReturnsTrue()
    {
        var inputs = new Dictionary<string, object?>
        {
            ["enabled"] = JsonSerializer.Deserialize<JsonElement>("true")
        };

        var found = inputs.TryGetBoolean("enabled", out var value);

        found.Should().BeTrue();
        value.Should().BeTrue();
    }

    [Fact]
    public void TryGetDateTimeOffset_WithRoundTripString_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var inputs = new Dictionary<string, object?>
        {
            ["startedAt"] = now.ToString("O")
        };

        var found = inputs.TryGetDateTimeOffset("startedAt", out var parsed);

        found.Should().BeTrue();
        parsed.Should().Be(now);
    }
}
