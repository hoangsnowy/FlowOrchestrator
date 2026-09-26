using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Tests.Abstractions;

/// <summary>
/// Pins <see cref="StepCollection.FindStep"/> to the exact semantics of the <c>string.Split</c>-based
/// implementation it replaced (#189): same result for every key shape, including the odd ones —
/// whitespace, empty segments, signed numbers, and numbers that do not follow a scoped step.
/// </summary>
/// <remarks>
/// The legacy algorithm is kept here verbatim as the oracle, so any divergence introduced by a future
/// optimisation of the span walk fails with the offending key in the test name.
/// </remarks>
public sealed class StepCollectionFindStepEquivalenceTests
{
    private static readonly StepCollection Steps = BuildManifest();

    public static TheoryData<string> Keys() =>
    [
        "prepare", "process", "finalize", "missing",
        "process.validate", "process.archive", "process.missing",
        "process.0", "process.0.validate", "process.12.archive", "process.12",
        "process.0.inner", "process.0.inner.3", "process.0.inner.3.leaf", "process.0.inner.3.missing",
        "process.0.inner.leaf", "process.inner.3.leaf",
        "prepare.0", "prepare.child", "0.process", "7",
        " process . 0 . validate ", "process..0..validate", "process.0.", ".process.0.validate",
        "process. .validate", "process.-1.validate", "process.+2.validate", "process.x.validate",
        "process.0.validate.extra", "", ".", "..", " ", "process .0",
    ];

    [Theory]
    [MemberData(nameof(Keys))]
    public void FindStep_matches_the_legacy_split_implementation(string key)
    {
        // Arrange
        var expected = LegacyFindStep(Steps, key);

        // Act
        var actual = Steps.FindStep(key);

        // Assert
        Assert.Same(expected, actual);
    }

    [Fact]
    public void FindStep_resolves_a_deeply_nested_runtime_key_to_its_template()
    {
        // Arrange
        var leaf = ((LoopStepMetadata)((LoopStepMetadata)Steps["process"]).Steps["inner"]).Steps["leaf"];

        // Act
        var found = Steps.FindStep("process.41.inner.7.leaf");

        // Assert
        Assert.Same(leaf, found);
    }

    private static StepCollection BuildManifest() => new()
    {
        ["prepare"] = new StepMetadata { Type = "Prepare" },
        ["process"] = new LoopStepMetadata
        {
            Type = "ForEach",
            Steps = new StepCollection
            {
                ["validate"] = new StepMetadata { Type = "Validate" },
                ["archive"] = new StepMetadata { Type = "Archive" },
                ["inner"] = new LoopStepMetadata
                {
                    Type = "ForEach",
                    Steps = new StepCollection { ["leaf"] = new StepMetadata { Type = "Leaf" } }
                },
                ["empty"] = new LoopStepMetadata { Type = "ForEach", Steps = new StepCollection() },
            }
        },
        ["finalize"] = new StepMetadata { Type = "Finalize" },
        ["0"] = new StepMetadata { Type = "NumericTopLevel" },
    };

    // ── The pre-#189 implementation, verbatim, as the oracle ─────────────────────

    private static StepMetadata? LegacyFindStep(StepCollection steps, string key)
    {
        if (steps.TryGetValue(key, out var step))
        {
            return step;
        }

        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return null;
        }

        return LegacyFindStepRecursive(steps, segments, 0, null);
    }

    private static StepMetadata? LegacyFindStepRecursive(
        IDictionary<string, StepMetadata> current,
        IReadOnlyList<string> segments,
        int index,
        StepMetadata? parentScopedStep)
    {
        if (index >= segments.Count)
        {
            return null;
        }

        if (int.TryParse(segments[index], out _) && parentScopedStep is IScopedStep scopedParent)
        {
            if (index == segments.Count - 1)
            {
                return parentScopedStep;
            }

            return LegacyFindStepRecursive(scopedParent.Steps, segments, index + 1, parentScopedStep);
        }

        if (!current.TryGetValue(segments[index], out var step) || step is null)
        {
            return null;
        }

        if (index == segments.Count - 1)
        {
            return step;
        }

        if (step is IScopedStep scoped && scoped.Steps is { Count: > 0 })
        {
            return LegacyFindStepRecursive(scoped.Steps, segments, index + 1, step);
        }

        return null;
    }
}
