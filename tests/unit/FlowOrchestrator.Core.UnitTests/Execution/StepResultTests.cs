using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using System.Text.Json;

namespace FlowOrchestrator.Core.Tests.Execution;

public class StepResultTests
{
    [Fact]
    public void DefaultStatus_IsSucceeded()
    {
        // Arrange

        // Act
        var result = new StepResult();

        // Assert
        Assert.Equal(StepStatus.Succeeded, result.Status);
    }

    [Fact]
    public void DefaultReThrow_IsFalse()
    {
        // Arrange

        // Act
        var result = new StepResult();

        // Assert
        Assert.False(result.ReThrow);
    }

    [Fact]
    public void DefaultDelayNextStep_IsNull()
    {
        // Arrange

        // Act
        var result = new StepResult();

        // Assert
        Assert.Null(result.DelayNextStep);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        // Arrange
        var delay = TimeSpan.FromSeconds(10);

        // Act
        var result = new StepResult
        {
            Key = "step1",
            Status = StepStatus.Failed,
            Result = new { data = 42 },
            FailedReason = "Something went wrong",
            ReThrow = true,
            DelayNextStep = delay
        };

        // Assert
        Assert.Equal("step1", result.Key);
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal("Something went wrong", result.FailedReason);
        Assert.True(result.ReThrow);
        Assert.Equal(delay, result.DelayNextStep);
    }

    [Fact]
    public void GenericStepResult_MapsValueToResult()
    {
        // Arrange

        // Act
        var result = new StepResult<TestPayload>
        {
            Key = "step2",
            Value = new TestPayload { Code = "A1" }
        };

        // Assert
        Assert.IsType<TestPayload>(result.Result);
        Assert.Equal("A1", result.Value!.Code);
    }

    [Fact]
    public void GenericStepResult_MapsResultToValue()
    {
        // Arrange
        var result = new StepResult<TestPayload> { Key = "step3" };
        var payloadElement = JsonSerializer.Deserialize<JsonElement>("{\"code\":\"B2\"}");

        // Act
        result.Result = payloadElement;

        // Assert
        Assert.NotNull(result.Value);
        Assert.Equal("B2", result.Value!.Code);
    }

    private sealed class TestPayload
    {
        public string Code { get; set; } = string.Empty;
    }
}
