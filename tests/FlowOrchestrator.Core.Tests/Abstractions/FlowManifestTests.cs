using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class FlowManifestTests
{
    [Fact]
    public void DefaultManifest_HasEmptyTriggersAndSteps()
    {
        var manifest = new FlowManifest();

        manifest.Triggers.Should().BeEmpty();
        manifest.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Manifest_CanHoldTriggersAndSteps()
    {
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

        manifest.Triggers.Should().ContainKey("manual");
        manifest.Steps.Should().ContainKey("step1");
    }

    [Fact]
    public void LoopStepMetadata_DefaultProperties()
    {
        var loop = new LoopStepMetadata();

        loop.ForEach.Should().BeNull();
        loop.ConcurrencyLimit.Should().Be(1);
        loop.Steps.Should().BeEmpty();
    }

    [Fact]
    public void TriggerMetadata_DefaultProperties()
    {
        var trigger = new TriggerMetadata { Type = TriggerType.Manual };

        trigger.Type.Should().Be(TriggerType.Manual);
        trigger.Inputs.Should().BeEmpty();
    }
}
