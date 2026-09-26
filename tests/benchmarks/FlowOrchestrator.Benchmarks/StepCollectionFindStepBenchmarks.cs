using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures <see cref="StepCollection.FindStep"/> on runtime loop keys (<c>"loop.3.child"</c>), the
/// shape <c>LoopBarrier.RunningLoopKeys</c> resolves once per status-map entry on every step
/// completion (#189, P0 item 2).
/// </summary>
/// <remarks>
/// <see cref="LegacySplit"/> is the pre-#189 implementation verbatim: a <c>string.Split</c> array plus
/// one string per segment on every call that misses the top-level key. <see cref="Shipped"/> walks the
/// key as <see cref="ReadOnlySpan{T}"/> segments and, on .NET 9+, probes each level through
/// <c>GetAlternateLookup&lt;ReadOnlySpan&lt;char&gt;&gt;</c>, so no segment string is ever created.
/// A top-level hit is included to confirm the fast path did not regress.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class StepCollectionFindStepBenchmarks
{
    private StepCollection _steps = null!;

    /// <summary>Key shape: top-level, one loop level, or two nested loop levels.</summary>
    [Params("finalize", "process.37.validate", "process.37.inner.4.leaf")]
    public string Key { get; set; } = null!;

    /// <summary>Builds a manifest with a top-level step and two nested ForEach scopes.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _steps = new StepCollection
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
                    }
                }
            },
            ["finalize"] = new StepMetadata { Type = "Finalize" },
        };
    }

    /// <summary>The pre-#189 <c>Split</c>-based implementation.</summary>
    [Benchmark(Baseline = true, Description = "legacy Split + recursion")]
    public StepMetadata? LegacySplit() => LegacyFindStep(_steps, Key);

    /// <summary>The shipped span walk.</summary>
    [Benchmark(Description = "span walk (shipped)")]
    public StepMetadata? Shipped() => _steps.FindStep(Key);

    private static StepMetadata? LegacyFindStep(StepCollection steps, string key)
    {
        if (steps.TryGetValue(key, out var step))
        {
            return step;
        }

        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length <= 1 ? null : LegacyRecursive(steps, segments, 0, null);
    }

    private static StepMetadata? LegacyRecursive(IDictionary<string, StepMetadata> current, IReadOnlyList<string> segments, int index, StepMetadata? parentScopedStep)
    {
        if (index >= segments.Count)
        {
            return null;
        }

        if (int.TryParse(segments[index], out _) && parentScopedStep is IScopedStep scopedParent)
        {
            return index == segments.Count - 1
                ? parentScopedStep
                : LegacyRecursive(scopedParent.Steps, segments, index + 1, parentScopedStep);
        }

        if (!current.TryGetValue(segments[index], out var step) || step is null)
        {
            return null;
        }

        if (index == segments.Count - 1)
        {
            return step;
        }

        return step is IScopedStep scoped && scoped.Steps is { Count: > 0 }
            ? LegacyRecursive(scoped.Steps, segments, index + 1, step)
            : null;
    }
}
