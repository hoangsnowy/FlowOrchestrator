using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepMetadataTests
{
    [Fact]
    public void ShouldExecute_EmptyRunAfter_ReturnsTrue()
    {
        // Arrange
        var step = new StepMetadata { Type = "A", RunAfter = new RunAfterCollection() };

        // Act
        var result = step.ShouldExecute("anyStep", StepStatus.Succeeded);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldExecute_MatchingStepAndStatus_ReturnsTrue()
    {
        // Arrange
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded, StepStatus.Failed } }
        };

        // Act
        var succeeded = step.ShouldExecute("step1", StepStatus.Succeeded);
        var failed = step.ShouldExecute("step1", StepStatus.Failed);

        // Assert
        Assert.True(succeeded);
        Assert.True(failed);
    }

    [Fact]
    public void ShouldExecute_StatusNotInAllowedList_ReturnsFalse()
    {
        // Arrange
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
        };

        // Act
        var result = step.ShouldExecute("step1", StepStatus.Failed);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldExecute_DifferentPrecedingStep_ReturnsFalse()
    {
        // Arrange
        var step = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
        };

        // Act
        var result = step.ShouldExecute("step2", StepStatus.Succeeded);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void DefaultInputs_IsEmptyDictionary()
    {
        // Arrange

        // Act
        var step = new StepMetadata { Type = "A" };

        // Assert
        Assert.Empty(step.Inputs);
    }

    [Fact]
    public void DefaultRunAfter_IsEmptyCollection()
    {
        // Arrange

        // Act
        var step = new StepMetadata { Type = "A" };

        // Assert
        Assert.Empty(step.RunAfter);
    }
}
