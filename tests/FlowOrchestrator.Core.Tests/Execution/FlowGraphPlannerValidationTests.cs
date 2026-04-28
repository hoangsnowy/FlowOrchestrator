using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FluentAssertions;
using NSubstitute;

namespace FlowOrchestrator.Core.Tests.Execution;

/// <summary>
/// Tests for <see cref="FlowGraphPlanner.Validate"/>, verifying that structural defects
/// in a flow manifest — cycles, missing dependencies, and missing entry steps — are
/// detected before any execution begins.
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
        // a depends on b; b depends on a — direct mutual dependency.
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

        var result = _sut.Validate(FlowWith(steps));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cycle"),
            "a mutual two-step dependency is a cycle and must be detected");
    }

    [Fact]
    public void Validate_DiamondWithBackEdge_ReportsError()
    {
        // Valid diamond: a → b, a → c, b → d, c → d
        // Back-edge added:   d → b, creating the cycle: b → d → b.
        var steps = new StepCollection
        {
            ["a"] = new StepMetadata { Type = "T" },   // entry
            ["b"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection
                {
                    ["a"] = [StepStatus.Succeeded],
                    ["d"] = [StepStatus.Succeeded]   // back-edge introduces cycle
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

        var result = _sut.Validate(FlowWith(steps));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cycle"),
            "a back-edge in the diamond graph creates a cycle that must be detected");
    }

    [Fact]
    public void Validate_StepWithMissingDependency_ReportsError()
    {
        // step2 declares a dependency on "nonexistent", which has no entry in the manifest.
        var steps = new StepCollection
        {
            ["step1"] = new StepMetadata { Type = "T" },
            ["step2"] = new StepMetadata
            {
                Type = "T",
                RunAfter = new RunAfterCollection { ["nonexistent"] = [StepStatus.Succeeded] }
            }
        };

        var result = _sut.Validate(FlowWith(steps));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("nonexistent"),
            "the name of the missing dependency must appear in the error message");
    }

    [Fact]
    public void Validate_NoEntryStep_ReportsError()
    {
        // Every step has RunAfter — no step can be the starting point.
        // Also implies a cycle (each step waits for the other).
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

        var result = _sut.Validate(FlowWith(steps));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("entry") || e.Contains("cycle"),
            "a graph with no entry step must produce a validation error");
    }

    [Fact]
    public void Validate_ValidDiamond_IsValid()
    {
        // Acyclic diamond: a (entry) → b, a → c, b → d, c → d.
        // All dependencies exist, no cycles, there is exactly one entry step.
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

        var result = _sut.Validate(FlowWith(steps));

        result.IsValid.Should().BeTrue(
            "a valid diamond-shaped DAG has an entry step, no cycles, and all dependencies exist");
        result.Errors.Should().BeEmpty();
    }
}
