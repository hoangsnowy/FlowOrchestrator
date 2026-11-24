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
    [Fact]
    public void Validate_TopLevelKeysDifferingOnlyByCase_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["Notify"] = new StepMetadata { Type = "T" },
            ["notify"] = new StepMetadata { Type = "T" }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'Notify'") && e.Contains("'notify'") && e.Contains("differ only by case"));
    }

    [Fact]
    public void Validate_LoopChildKeysDifferingOnlyByCase_ReportsErrorWithTheScopedPath()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection
                {
                    ["child"] = new StepMetadata { Type = "T" },
                    ["CHILD"] = new StepMetadata { Type = "T" }
                }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'loop.child'") && e.Contains("'loop.CHILD'"));
    }

    [Fact]
    public void Validate_SameKeyInDifferentScopes_IsValid()
    {
        // Arrange — "child" under two different loops resolves to distinct runtime keys, so it is not a collision.
        var steps = new StepCollection
        {
            ["first"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection { ["child"] = new StepMetadata { Type = "T" } }
            },
            ["second"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["first"] = [StepStatus.Succeeded] },
                Steps = new StepCollection { ["Child"] = new StepMetadata { Type = "T" } }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("x:input")]
    [InlineData("__trigger:data")]
    public void Validate_TopLevelKeyContainingColon_ReportsError(string key)
    {
        // Arrange — ':' is the outputs-key separator; "x:input" would overwrite step x's persisted inputs.
        var steps = new StepCollection
        {
            ["x"] = new StepMetadata { Type = "T" },
            [key] = new StepMetadata { Type = "T" }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains($"'{key}'") && e.Contains("':'"));
    }

    [Fact]
    public void Validate_LoopChildKeyContainingColon_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            ["loop"] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection { ["a:b"] = new StepMetadata { Type = "T" } }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'loop.a:b'") && e.Contains("':'"));
    }

    [Fact]
    public void Validate_KeyAtTheLengthLimit_IsValid()
    {
        // Arrange
        var steps = new StepCollection
        {
            [new string('k', FlowGraphPlanner.MaxStepKeyLength)] = new StepMetadata { Type = "T" }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_KeyOverTheLengthLimit_ReportsError()
    {
        // Arrange
        var steps = new StepCollection
        {
            [new string('k', FlowGraphPlanner.MaxStepKeyLength + 1)] = new StepMetadata { Type = "T" }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("maximum is 250"));
    }

    [Fact]
    public void Validate_NestedPathOverTheLengthLimit_ReportsError()
    {
        // Arrange — each segment is short enough, the flattened "loop.child" path is not.
        var loopKey = new string('l', 200);
        var childKey = new string('c', 60);
        var steps = new StepCollection
        {
            [loopKey] = new LoopStepMetadata
            {
                Type = "ForEach",
                Steps = new StepCollection { [childKey] = new StepMetadata { Type = "T" } }
            }
        };

        // Act
        var result = _sut.Validate(FlowWith(steps));

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains($"'{loopKey}.{childKey}'") && e.Contains("261 characters"));
    }
}
