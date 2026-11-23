using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Tests for <see cref="FlowGraphPlanner.Validate"/>, verifying that structural defects
/// in a flow manifest — cycles, missing dependencies, missing entry steps, and malformed
/// top-level step keys — are detected before any execution begins.
/// </summary>
public sealed class FlowGraphPlannerValidationTests
{
    private readonly FlowGraphPlanner _sut = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IFlowDefinition FlowWith(StepCollection steps)
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        flow.Manifest.Returns(new FlowManifest { Steps = steps });
        return flow;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_TwoStepDirectCycle_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] }
            },
            ["b"] = new StepMetadata
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
    public void Validate_DiamondWithBackEdge_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata { Type = "T" },
            ["b"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection
                {
                    ["a"] = [StepStatus.Succeeded],
                    ["d"] = [StepStatus.Succeeded]
                }
            },
            ["c"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["d"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection
                {
                    ["b"] = [StepStatus.Succeeded],
                    ["c"] = [StepStatus.Succeeded]
                }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cycle"));
    }

    [Fact]
    public void Validate_StepWithMissingDependency_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "T" },
            ["step2"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["nonexistent"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("nonexistent"));
    }

    [Fact]
    public void Validate_NoEntryStep_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["b"] = [StepStatus.Succeeded] }
            },
            ["b"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("entry") || e.Contains("cycle"));
    }

    [Fact]
    public void Validate_ValidDiamond_IsValid()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata { Type = "T" },
            ["b"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["c"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["a"] = [StepStatus.Succeeded] }
            },
            ["d"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection
                {
                    ["b"] = [StepStatus.Succeeded],
                    ["c"] = [StepStatus.Succeeded]
                }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_TopLevelKeyContainingDot_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["entry"] = new StepMetadata { Type = "T" },
            ["odd.key"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["entry"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'odd.key'") && e.Contains("'.'"));
    }

    [Fact]
    public void Validate_EmptyTopLevelKey_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["entry"] = new StepMetadata { Type = "T" },
            [""] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["entry"] = [StepStatus.Succeeded] }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty key"));
    }

    [Fact]
    public void Validate_LoopWithChildSteps_IsValid()
    {
        // Arrange — the flattened child path "loop.child" contains a dot, but only top-level keys
        // are subject to the key-shape check.
        var steps = new StepCollection
        {
            ["entry"] = new StepMetadata { Type = "T" },
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["entry"] = [StepStatus.Succeeded] },
                Steps = new StepCollection
                {
                    ["child"] = new StepMetadata { Type = "T" }
                }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
