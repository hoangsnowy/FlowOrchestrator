using System.Text.Json;
using FlowOrchestrator.Core.Notifications;

namespace FlowOrchestrator.Core.Tests.Notifications;

/// <summary>
/// Verifies the <see cref="FlowLifecycleEvent"/> hierarchy: discriminator stability,
/// JSON round-trip, and per-subtype payload preservation. The discriminator strings are
/// public API (used as SSE <c>event:</c> field values) so tests pin them explicitly.
/// </summary>
public sealed class FlowLifecycleEventTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void RunStartedEvent_has_stable_discriminator()
    {
        // Arrange
        var evt = new RunStartedEvent { RunId = Guid.NewGuid(), FlowId = Guid.NewGuid(), FlowName = "X", TriggerKey = "manual" };

        // Act
        var type = evt.Type;

        // Assert
        Assert.Equal("run.started", type);
    }

    [Fact]
    public void StepCompletedEvent_has_stable_discriminator()
    {
        // Arrange
        var evt = new StepCompletedEvent { RunId = Guid.NewGuid(), StepKey = "s", Status = "Succeeded" };

        // Act
        var type = evt.Type;

        // Assert
        Assert.Equal("step.completed", type);
    }

    [Fact]
    public void StepRetriedEvent_has_stable_discriminator()
    {
        // Arrange
        var evt = new StepRetriedEvent { RunId = Guid.NewGuid(), StepKey = "s" };

        // Act
        var type = evt.Type;

        // Assert
        Assert.Equal("step.retried", type);
    }

    [Fact]
    public void RunCompletedEvent_has_stable_discriminator()
    {
        // Arrange
        var evt = new RunCompletedEvent { RunId = Guid.NewGuid(), Status = "Succeeded" };

        // Act
        var type = evt.Type;

        // Assert
        Assert.Equal("run.completed", type);
    }

    [Fact]
    public void RunStartedEvent_round_trips_through_JSON()
    {
        // Arrange
        var original = new RunStartedEvent
        {
            RunId = Guid.NewGuid(),
            FlowId = Guid.NewGuid(),
            FlowName = "OrderFlow",
            TriggerKey = "webhook",
            At = DateTimeOffset.UtcNow
        };

        // Act
        var json = JsonSerializer.Serialize<object>(original, _jsonOptions);
        var rt = JsonSerializer.Deserialize<RunStartedEvent>(json, _jsonOptions);

        // Assert
        Assert.NotNull(rt);
        Assert.Equal(original.RunId, rt!.RunId);
        Assert.Equal(original.FlowId, rt.FlowId);
        Assert.Equal(original.FlowName, rt.FlowName);
        Assert.Equal(original.TriggerKey, rt.TriggerKey);
    }

    [Fact]
    public void StepCompletedEvent_failed_status_preserves_failure_reason()
    {
        // Arrange
        var original = new StepCompletedEvent
        {
            RunId = Guid.NewGuid(),
            StepKey = "validate",
            Status = "Failed",
            FailedReason = "schema mismatch on field 'amount'"
        };

        // Act
        var json = JsonSerializer.Serialize<object>(original, _jsonOptions);
        var rt = JsonSerializer.Deserialize<StepCompletedEvent>(json, _jsonOptions);

        // Assert
        Assert.NotNull(rt);
        Assert.Equal("Failed", rt!.Status);
        Assert.Equal("schema mismatch on field 'amount'", rt.FailedReason);
    }

    [Fact]
    public void At_defaults_to_close_to_now()
    {
        // Arrange
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Act
        var evt = new RunCompletedEvent { RunId = Guid.NewGuid(), Status = "Succeeded" };

        // Assert
        // Default At is set in the base record initialiser.
        Assert.True(evt.At >= before);
        Assert.True(evt.At <= DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
