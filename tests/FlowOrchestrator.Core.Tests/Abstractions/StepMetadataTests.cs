using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepMetadataTests
{
    [Fact]
    public void ShouldExecute_EmptyRunAfter_ReturnsTrue()
    {
        var step = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection() };

        step.ShouldExecute("anyStep", StepStatus.Succeeded).Should().BeTrue();
    }

    [Fact]
    public void ShouldExecute_MatchingStepAndStatus_ReturnsTrue()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded, StepStatus.Failed } }
        };

        step.ShouldExecute("step1", StepStatus.Succeeded).Should().BeTrue();
        step.ShouldExecute("step1", StepStatus.Failed).Should().BeTrue();
    }

    [Fact]
    public void ShouldExecute_StatusNotInAllowedList_ReturnsFalse()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
        };

        step.ShouldExecute("step1", StepStatus.Failed).Should().BeFalse();
    }

    [Fact]
    public void ShouldExecute_DifferentPrecedingStep_ReturnsFalse()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
        };

        step.ShouldExecute("step2", StepStatus.Succeeded).Should().BeFalse();
    }

    [Fact]
    public void DefaultInputs_IsEmptyDictionary()
    {
        var step = new StepMetadata { Type = "A" };

        step.Inputs.Should().BeEmpty();
    }

    [Fact]
    public void DefaultRunAfter_IsEmptyCollection()
    {
        var step = new StepMetadata { Type = "A" };

        step.RunAfter.Should().BeEmpty();
    }
}
