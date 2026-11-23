using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepMetadataTests
{
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
