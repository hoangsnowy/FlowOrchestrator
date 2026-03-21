using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;
using System.Text.Json;

namespace FlowOrchestrator.Core.Tests.Execution;

public class StepResultTests
{
    [Fact]
    public void DefaultStatus_IsSucceeded()
    {
        var result = new StepResult();

        result.Status.Should().Be(StepStatus.Succeeded);
    }

    [Fact]
    public void DefaultReThrow_IsFalse()
    {
        var result = new StepResult();

        result.ReThrow.Should().BeFalse();
    }

    [Fact]
    public void DefaultDelayNextStep_IsNull()
    {
        var result = new StepResult();

        result.DelayNextStep.Should().BeNull();
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var delay = TimeSpan.FromSeconds(10);
        var result = new StepResult
        {
            Key = "step1",
            Status = StepStatus.Failed,
            Result = new { data = 42 },
            FailedReason = "Something went wrong",
            ReThrow = true,
            DelayNextStep = delay
        };

        result.Key.Should().Be("step1");
        result.Status.Should().Be(StepStatus.Failed);
        result.Result.Should().NotBeNull();
        result.FailedReason.Should().Be("Something went wrong");
        result.ReThrow.Should().BeTrue();
        result.DelayNextStep.Should().Be(delay);
    }

    [Fact]
    public void GenericStepResult_MapsValueToResult()
    {
        var result = new StepResult<TestPayload>
        {
            Key = "step2",
            Value = new TestPayload { Code = "A1" }
        };

        result.Result.Should().BeOfType<TestPayload>();
        result.Value!.Code.Should().Be("A1");
    }

    [Fact]
    public void GenericStepResult_MapsResultToValue()
    {
        var result = new StepResult<TestPayload> { Key = "step3" };
        var payloadElement = JsonSerializer.Deserialize<JsonElement>("{\"code\":\"B2\"}");

        result.Result = payloadElement;

        result.Value.Should().NotBeNull();
        result.Value!.Code.Should().Be("B2");
    }

    private sealed class TestPayload
    {
        public string Code { get; set; } = string.Empty;
    }
}
