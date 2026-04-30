using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class FlowManifestTests
{
    [Fact]
    public void DefaultManifest_HasEmptyTriggersAndSteps()
    {
        // Arrange

        // Act
        var manifest = new FlowManifest();

        // Assert
        Assert.Empty(manifest.Triggers);
        Assert.Empty(manifest.Steps);
    }

    [Fact]
    public void Manifest_CanHoldTriggersAndSteps()
    {
        // Arrange

        // Act
        var manifest = new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
            },
            Steps = new StepCollection
            {
                ["step1"] = new StepMetadata { Type = "LogMessage" }
            }
        };

        // Assert
        Assert.True(manifest.Triggers.ContainsKey("manual"));
        Assert.True(manifest.Steps.ContainsKey("step1"));
    }

    [Fact]
    public void LoopStepMetadata_DefaultProperties()
    {
        // Arrange

        // Act
        var loop = new LoopStepMetadata();

        // Assert
        Assert.Null(loop.ForEach);
        Assert.Equal(1, loop.ConcurrencyLimit);
        Assert.Empty(loop.Steps);
    }

    [Fact]
    public void TriggerMetadata_DefaultProperties()
    {
        // Arrange

        // Act
        var trigger = new TriggerMetadata { Type = TriggerType.Manual };

        // Assert
        Assert.Equal(TriggerType.Manual, trigger.Type);
        Assert.Empty(trigger.Inputs);
    }
}
