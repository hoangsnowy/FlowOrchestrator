using System.Text.Json;

namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Shared path-navigation helpers used by trigger-body and step-output expression resolvers.
/// </summary>
internal static class ExpressionPathHelper
{
    /// <summary>
    /// Walks <paramref name="payload"/> along <paramref name="path"/>, supporting
    /// dot-separated property names (<c>a.b.c</c>) and bracket array indices
    /// (<c>items[0]</c> normalised to <c>items.0</c> internally).
    /// Returns <see langword="false"/> when any segment is not found.
    /// </summary>
    /// <remarks>
    /// Property matching is case-sensitive first, then falls back to an ordinal
    /// case-insensitive scan. The fallback exists because step outputs and trigger payloads
    /// are persisted with <see cref="JsonSerializerDefaults.Web"/> (camelCase), while
    /// manifests are authored in C# and naturally spell the path in the CLR's PascalCase —
    /// so <c>@steps('scan_vision').output.Location</c> has to find the stored
    /// <c>"location"</c> property. This mirrors
    /// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> on the
    /// deserialisation side and never changes the meaning of a path that already matched
    /// exactly.
    /// </remarks>
    internal static bool TryResolvePath(JsonElement payload, string path, out JsonElement target)
    {
        target = payload;

        // Walked as spans rather than normalised into a new string and Split into an array.
        // The old shape allocated two intermediate strings (one per Replace that actually matched),
        // a string[] and one string per segment — on every expression, on every step input, on every
        // step. '.', '[' and ']' are all treated as separators, which is exactly what replacing
        // '[' with '.' and stripping ']' used to achieve, and empty segments are skipped the way
        // RemoveEmptyEntries did.
        var remaining = path.AsSpan();
        while (!remaining.IsEmpty)
        {
            var cut = remaining.IndexOfAny('.', '[', ']');
            ReadOnlySpan<char> segment;
            if (cut < 0)
            {
                segment = remaining;
                remaining = default;
            }
            else
            {
                segment = remaining[..cut];
                remaining = remaining[(cut + 1)..];
            }

            segment = segment.Trim();
            if (segment.IsEmpty)
            {
                continue;
            }

            if (target.ValueKind == JsonValueKind.Object && TryGetPropertyRelaxed(target, segment, out var prop))
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

    /// <summary>
    /// Looks up <paramref name="name"/> on a JSON object, preferring an exact match and
    /// falling back to the first ordinal case-insensitive match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact-match attempt runs first so a payload that genuinely carries both
    /// <c>"location"</c> and <c>"Location"</c> still resolves to the spelling the
    /// expression asked for.
    /// </para>
    /// <para>
    /// The fallback drives the enumerator directly rather than through
    /// <c>Where(...).FirstOrDefault()</c>. A C#-authored manifest spells paths in PascalCase while
    /// payloads are persisted camelCase, so the fallback is the <i>common</i> path, not the rare
    /// one — and the LINQ shape allocated two iterators plus a boxed nullable per segment, per
    /// expression, per step execution. <see cref="JsonElement.ObjectEnumerator"/> is a struct, so
    /// this loop allocates nothing and short-circuits identically.
    /// </para>
    /// </remarks>
    private static bool TryGetPropertyRelaxed(JsonElement target, ReadOnlySpan<char> name, out JsonElement value)
    {
        // The span overloads of TryGetProperty and NameEquals compare against the payload's UTF-8
        // bytes directly, so neither the exact-match attempt nor the case-insensitive sweep has to
        // materialise the segment as a string.
        if (target.TryGetProperty(name, out value))
        {
            return true;
        }

        var enumerator = target.EnumerateObject();
        while (enumerator.MoveNext())
        {
            if (!enumerator.Current.Name.AsSpan().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = enumerator.Current.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Serialises <paramref name="value"/> to a <see cref="JsonElement"/>.
    /// Existing elements are cloned to avoid ownership issues after the source document is disposed.
    /// </summary>
    internal static JsonElement ToJsonElement(object value, JsonSerializerOptions options)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement<object?>(null, options)
                : element.Clone();
        }

        return JsonSerializer.SerializeToElement(value, value.GetType(), options);
    }
}
