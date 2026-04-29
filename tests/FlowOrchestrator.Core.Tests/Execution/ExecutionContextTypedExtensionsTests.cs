using System.Text.Json;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Tests.Execution;

public class ExecutionContextTypedExtensionsTests
{
    [Fact]
    public void GetTriggerDataAs_WithJsonElement_ReturnsTypedObject()
    {
        // Arrange
        var data = JsonSerializer.Deserialize<JsonElement>("{\"jobId\":\"JOB-1\",\"attempt\":3}");
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = data };

        // Act
        var value = context.GetTriggerDataAs<TriggerPayload>();

        // Assert
        Assert.NotNull(value);
        Assert.Equal("JOB-1", value!.JobId);
        Assert.Equal(3, value.Attempt);
    }

    [Fact]
    public void GetTriggerDataAs_WithNullTriggerData_ReturnsDefault()
    {
        // Arrange
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = null };

        // Act
        var value = context.GetTriggerDataAs<TriggerPayload>();

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public void TryGetTriggerDataAs_WithInvalidShape_ReturnsFalse()
    {
        // Arrange
        var data = JsonSerializer.Deserialize<JsonElement>("{\"attempt\":\"x\"}");
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = data };

        // Act
        var success = context.TryGetTriggerDataAs<TriggerPayload>(out var value);

        // Assert
        Assert.False(success);
        Assert.Null(value);
    }

    private sealed class TriggerPayload
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
    }
}
