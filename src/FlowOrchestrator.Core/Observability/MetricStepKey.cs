using System.Collections.Concurrent;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Maps a runtime step key to the bounded value used for the <c>step_key</c> metric tag: the manifest
/// (template) key, with loop-iteration index segments removed (<c>"loop.17.child"</c> → <c>"loop.child"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Tagging metrics with the runtime key made every ForEach iteration a new time series — cardinality
/// proportional to collection size, which OpenTelemetry SDKs cap (2,000 series per instrument by default)
/// before folding everything into an overflow series. Traces and logs keep the full runtime key; only
/// metric tags use this.
/// </para>
/// <para>
/// Segment semantics match the engine's own template resolution (<c>FlowGraphPlanner.RemoveNumericSegments</c>):
/// a segment that parses as an <see cref="int"/> is a loop index.
/// </para>
/// <para>
/// <b>Cost per call: O(key length), allocation-free in steady state.</b> A key with no numeric segment —
/// every key outside a loop — is returned as is. A loop key's template is composed on the stack and looked
/// up through a span alternate lookup, so its string is allocated once per distinct template (bounded by
/// the manifests, capped at <see cref="MaxCachedTemplates"/> as a guard), not once per call.
/// </para>
/// </remarks>
internal static class MetricStepKey
{
    /// <summary>Upper bound on cached template strings; beyond it a template is still returned, just not cached.</summary>
    internal const int MaxCachedTemplates = 4096;

    private const int StackBufferLength = 256;

    private static readonly ConcurrentDictionary<string, string> Templates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> TemplateLookup =
        Templates.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Returns the template key for <paramref name="runtimeKey"/>, for use as the <c>step_key</c> metric tag.</summary>
    /// <param name="runtimeKey">The runtime step key, e.g. <c>"process_items.3.validate"</c>.</param>
    /// <returns>
    /// The key with numeric segments removed; <paramref name="runtimeKey"/> itself when it has none, or when
    /// removing them would leave nothing.
    /// </returns>
    public static string For(string runtimeKey)
    {
        if (string.IsNullOrEmpty(runtimeKey) || !HasNumericSegment(runtimeKey))
        {
            return runtimeKey;
        }

        if (runtimeKey.Length > StackBufferLength)
        {
            // Pathologically long keys: correctness over the allocation budget.
            return Compose(runtimeKey, new char[runtimeKey.Length]);
        }

        Span<char> buffer = stackalloc char[StackBufferLength];
        var length = Write(runtimeKey, buffer);
        if (length == 0)
        {
            return runtimeKey;
        }

        var template = buffer[..length];
        if (TemplateLookup.TryGetValue(template, out var cached))
        {
            return cached;
        }

        var created = template.ToString();
        if (Templates.Count < MaxCachedTemplates)
        {
            Templates.TryAdd(created, created);
        }

        return created;
    }

    private static string Compose(string runtimeKey, Span<char> buffer)
    {
        var length = Write(runtimeKey, buffer);
        return length == 0 ? runtimeKey : buffer[..length].ToString();
    }

    /// <summary>Writes the non-numeric segments of <paramref name="key"/>, dot-joined, into <paramref name="destination"/>.</summary>
    /// <returns>The number of characters written.</returns>
    private static int Write(string key, Span<char> destination)
    {
        var written = 0;
        var rest = key.AsSpan();
        while (true)
        {
            var dot = rest.IndexOf('.');
            var segment = dot < 0 ? rest : rest[..dot];
            if (!int.TryParse(segment, out _))
            {
                if (written > 0)
                {
                    destination[written++] = '.';
                }

                segment.CopyTo(destination[written..]);
                written += segment.Length;
            }

            if (dot < 0)
            {
                return written;
            }

            rest = rest[(dot + 1)..];
        }
    }

    private static bool HasNumericSegment(string key)
    {
        if (!key.Contains('.', StringComparison.Ordinal))
        {
            // A single segment: a numeric top-level key would template to nothing, so it is kept anyway.
            return false;
        }

        var rest = key.AsSpan();
        while (true)
        {
            var dot = rest.IndexOf('.');
            var segment = dot < 0 ? rest : rest[..dot];
            if (int.TryParse(segment, out _))
            {
                return true;
            }

            if (dot < 0)
            {
                return false;
            }

            rest = rest[(dot + 1)..];
        }
    }
}
