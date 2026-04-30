using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Edge-case tests for <see cref="FlowGraphPlanner.Validate"/> covering structural defects
/// not exercised by the primary <c>FlowGraphPlannerValidationTests</c> suite:
/// self-cycles, three-node cycles, empty step collections, and steps with empty type names.
/// These guard the manifest-validation contract (Section G7).
/// </summary>
public sealed class FlowGraphPlannerEdgeCaseTests
{
    private readonly FlowGraphPlanner _sut = new();

    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    [Fact]
    public void Validate_SelfCycle_ReportsCycleError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cycle"));
    }

    [Fact]
    public void Validate_ThreeStepCycle_ReportsCycleError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["c"] = [StepStatus.Succeeded] }
            },
            ["b"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["c"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cycle"));
    }

    [Fact]
    public void Validate_EmptyStepCollection_ReportsNoStepsError()
    {
        // Arrange
        var steps = new StepCollection();

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no steps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_StepWithEmptyType_ReportsEmptyTypeError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata { Type = "" }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_StepWithWhitespaceType_ReportsEmptyTypeError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata { Type = "   " }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty type", StringComparison.OrdinalIgnoreCase));
    }
}
