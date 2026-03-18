using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;

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
}
