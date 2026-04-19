using FlowOrchestrator.Core.Abstractions;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepCollectionTests
{
    [Fact]
    public void FindStep_DirectKey_ReturnsStep()
    {
        var step = new StepMetadata { Type = "LogMessage" };
        var collection = new StepCollection { ["step1"] = step };

        collection.FindStep("step1").Should().BeSameAs(step);
    }

    [Fact]
    public void FindStep_MissingKey_ReturnsNull()
    {
        var collection = new StepCollection { ["step1"] = new StepMetadata { Type = "A" } };

        collection.FindStep("missing").Should().BeNull();
    }

    [Fact]
    public void FindStep_NestedKey_ReturnsChildStep()
    {
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        collection.FindStep("parent.child").Should().BeSameAs(child);
    }

    [Fact]
    public void FindStep_NestedKey_ParentNotScoped_ReturnsNull()
    {
        var parent = new StepMetadata { Type = "Simple" };
        var collection = new StepCollection { ["parent"] = parent };

        collection.FindStep("parent.child").Should().BeNull();
    }

    [Fact]
    public void FindStep_DeeplyNestedKey_ReturnsCorrectStep()
    {
        var grandchild = new StepMetadata { Type = "GrandchildType" };
        var child = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["grandchild"] = grandchild }
        };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        collection.FindStep("parent.child.grandchild").Should().BeSameAs(grandchild);
    }

    [Fact]
    public void FindStep_RuntimeLoopKey_WithIndex_ReturnsChildStep()
    {
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        collection.FindStep("parent.0.child").Should().BeSameAs(child);
    }

    [Fact]
    public void FindNextStep_WithRunAfter_ReturnsSuccessor()
    {
        var step1 = new StepMetadata { Type = "A" };
        var step2 = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["step1"] = new[] { StepStatus.Succeeded } }
        };
        var collection = new StepCollection
        {
            ["step1"] = step1,
            ["step2"] = step2
        };

        collection.FindNextStep("step1").Should().BeSameAs(step2);
    }

    [Fact]
    public void FindNextStep_NoSuccessor_ReturnsNull()
    {
        var step1 = new StepMetadata { Type = "A" };
        var collection = new StepCollection { ["step1"] = step1 };

        collection.FindNextStep("step1").Should().BeNull();
    }

    [Fact]
    public void FindNextStep_EmptyRunAfter_ReturnsNull()
    {
        var step1 = new StepMetadata { Type = "A" };
        var step2 = new StepMetadata
        {
            Type = "B",
            RunAfter = new RunAfterCollection { ["other"] = new[] { StepStatus.Succeeded } }
        };
        var collection = new StepCollection
        {
            ["step1"] = step1,
            ["step2"] = step2
        };

        collection.FindNextStep("step1").Should().BeNull();
    }

    [Fact]
    public void FindParentStep_NestedKey_ReturnsParent()
    {
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        collection.FindParentStep("parent.child").Should().BeSameAs(parent);
    }

    [Fact]
    public void FindParentStep_TopLevelKey_ReturnsNull()
    {
        var collection = new StepCollection { ["step1"] = new StepMetadata { Type = "A" } };

        collection.FindParentStep("step1").Should().BeNull();
    }
}
