using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Before/after for the path walk inside <c>ExpressionPathHelper.TryResolvePath</c>, measured in one
/// run so the two shapes are compared on the same machine under the same conditions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StepInputResolutionBenchmarks"/> measures the whole resolution pipeline and therefore
/// only ever produces an "after" column once the implementation changes. This isolates the path walk
/// itself and keeps a runnable record of the shape that was replaced.
/// </para>
/// <para>
/// The old walk normalised the path into a new string with two <c>string.Replace</c> calls, then
/// <c>Split('.')</c> — up to two intermediate strings, a <c>string[]</c>, and one string per segment,
/// on every expression of every step input of every step. The new one walks the original string as
/// <see cref="ReadOnlySpan{T}"/> and looks properties up through the span overloads, which compare
/// against the payload's UTF-8 bytes without materialising the segment.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class ExpressionPathWalkBenchmarks
{
    /// <summary>Path depth under test — how many segments have to be walked.</summary>
    [Params(1, 3, 6)]
    public int Depth { get; set; }

    private JsonElement _payload;
    private string _path = null!;

    /// <summary>Builds a nested payload and the dotted path that reaches its deepest leaf.</summary>
    [GlobalSetup]
    public void Setup()
    {
        // Nest Depth levels deep, with the leaf carrying the value the path resolves to.
        var json = "\"leaf-value\"";
        var path = new List<string>();
        for (var d = Depth - 1; d >= 0; d--)
        {
            var name = $"level{d}";
            path.Insert(0, name);
            json = $"{{\"{name}\":{json},\"noise{d}\":123}}";
        }

        _payload = JsonSerializer.Deserialize<JsonElement>(json);
        _path = string.Join('.', path);
    }

    /// <summary>The walk as it was: normalise into a new string, split, then look up by string key.</summary>
    [Benchmark(Baseline = true, Description = "BEFORE: Replace + Split, string-keyed lookup")]
    public bool LegacyWalk()
    {
        var target = _payload;

        var normalizedPath = _path
            .Replace("[", ".", StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (target.ValueKind == JsonValueKind.Object && LegacyGetProperty(target, segment, out var prop))
            {
                target = prop;
                continue;
            }

            if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var idx) && idx >= 0 && idx < target.GetArrayLength())
            {
                target = target[idx];
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>The shipped walk: spans throughout, no intermediate strings.</summary>
    [Benchmark(Description = "AFTER: ReadOnlySpan walk, span-keyed lookup")]
    public bool SpanWalk() => ExpressionPathHelper.TryResolvePath(_payload, _path, out _);

    /// <summary>The original relaxed property lookup, keyed by <see cref="string"/>.</summary>
    private static bool LegacyGetProperty(JsonElement target, string name, out JsonElement value)
    {
        if (target.TryGetProperty(name, out value))
        {
            return true;
        }

        var enumerator = target.EnumerateObject();
        while (enumerator.MoveNext())
        {
            if (!string.Equals(enumerator.Current.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = enumerator.Current.Value;
            return true;
        }

        value = default;
        return false;
    }
}
