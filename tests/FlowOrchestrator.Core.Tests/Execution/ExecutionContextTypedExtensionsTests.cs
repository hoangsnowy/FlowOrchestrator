using System.Text.Json;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Execution;

public class ExecutionContextTypedExtensionsTests
{
    [Fact]
    public void GetTriggerDataAs_WithJsonElement_ReturnsTypedObject()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("{\"jobId\":\"JOB-1\",\"attempt\":3}");
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = data };

        var value = context.GetTriggerDataAs<TriggerPayload>();

        value.Should().NotBeNull();
        value!.JobId.Should().Be("JOB-1");
        value.Attempt.Should().Be(3);
    }

    [Fact]
    public void GetTriggerDataAs_WithNullTriggerData_ReturnsDefault()
    {
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = null };

        var value = context.GetTriggerDataAs<TriggerPayload>();

        value.Should().BeNull();
    }

    [Fact]
    public void TryGetTriggerDataAs_WithInvalidShape_ReturnsFalse()
    {
        var data = JsonSerializer.Deserialize<JsonElement>("{\"attempt\":\"x\"}");
        var context = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid(), TriggerData = data };

        var success = context.TryGetTriggerDataAs<TriggerPayload>(out var value);

        success.Should().BeFalse();
        value.Should().BeNull();
    }

    private sealed class TriggerPayload
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
    }
}
