using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepMetadataTests
{
    [Fact]
    public void ShouldExecute_EmptyRunAfter_ReturnsTrue()
    {
        var step = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection() };

        step.ShouldExecute("anyStep", "Succeeded").Should().BeTrue();
    }

    [Fact]
    public void ShouldExecute_MatchingStepAndStatus_ReturnsTrue()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { "Succeeded", "Failed" } }
        };

        step.ShouldExecute("step1", "Succeeded").Should().BeTrue();
        step.ShouldExecute("step1", "Failed").Should().BeTrue();
    }

    [Fact]
    public void ShouldExecute_StatusNotInAllowedList_ReturnsFalse()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { "Succeeded" } }
        };

        step.ShouldExecute("step1", "Failed").Should().BeFalse();
    }

    [Fact]
    public void ShouldExecute_DifferentPrecedingStep_ReturnsFalse()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { "Succeeded" } }
        };

        step.ShouldExecute("step2", "Succeeded").Should().BeFalse();
    }

    [Fact]
    public void ShouldExecute_CaseInsensitiveStatusMatch()
    {
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { "Succeeded" } }
        };

        step.ShouldExecute("step1", "succeeded").Should().BeTrue();
        step.ShouldExecute("step1", "SUCCEEDED").Should().BeTrue();
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
