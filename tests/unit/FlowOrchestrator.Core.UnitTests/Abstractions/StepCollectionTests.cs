using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

public class StepCollectionTests
{
    [Fact]
    public void FindStep_DirectKey_ReturnsStep()
    {
        // Arrange
        var step = new StepMetadata { Type = "LogMessage" };
        var collection = new StepCollection { ["step1"] = step };

        // Act
        var result = collection.FindStep("step1");

        // Assert
        Assert.Same(step, result);
    }

    [Fact]
    public void FindStep_MissingKey_ReturnsNull()
    {
        // Arrange
        var collection = new StepCollection { ["step1"] = new StepMetadata { Type = "A" } };

        // Act
        var result = collection.FindStep("missing");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindStep_NestedKey_ReturnsChildStep()
    {
        // Arrange
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        // Act
        var result = collection.FindStep("parent.child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void FindStep_NestedKey_ParentNotScoped_ReturnsNull()
    {
        // Arrange
        var parent = new StepMetadata { Type = "Simple" };
        var collection = new StepCollection { ["parent"] = parent };

        // Act
        var result = collection.FindStep("parent.child");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindStep_DeeplyNestedKey_ReturnsCorrectStep()
    {
        // Arrange
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

        // Act
        var result = collection.FindStep("parent.child.grandchild");

        // Assert
        Assert.Same(grandchild, result);
    }

    [Fact]
    public void FindStep_RuntimeLoopKey_WithIndex_ReturnsChildStep()
    {
        // Arrange
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        // Act
        var result = collection.FindStep("parent.0.child");

        // Assert
        Assert.Same(child, result);
    }

    [Fact]
    public void FindNextStep_WithRunAfter_ReturnsSuccessor()
    {
        // Arrange
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

        // Act
        var result = collection.FindNextStep("step1");

        // Assert
        Assert.Same(step2, result);
    }

    [Fact]
    public void FindNextStep_NoSuccessor_ReturnsNull()
    {
        // Arrange
        var step1 = new StepMetadata { Type = "A" };
        var collection = new StepCollection { ["step1"] = step1 };

        // Act
        var result = collection.FindNextStep("step1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindNextStep_EmptyRunAfter_ReturnsNull()
    {
        // Arrange
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

        // Act
        var result = collection.FindNextStep("step1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindParentStep_NestedKey_ReturnsParent()
    {
        // Arrange
        var child = new StepMetadata { Type = "ChildType" };
        var parent = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection { ["child"] = child }
        };
        var collection = new StepCollection { ["parent"] = parent };

        // Act
        var result = collection.FindParentStep("parent.child");

        // Assert
        Assert.Same(parent, result);
    }

    [Fact]
    public void FindParentStep_TopLevelKey_ReturnsNull()
    {
        // Arrange
        var collection = new StepCollection { ["step1"] = new StepMetadata { Type = "A" } };

        // Act
        var result = collection.FindParentStep("step1");

        // Assert
        Assert.Null(result);
    }
}
