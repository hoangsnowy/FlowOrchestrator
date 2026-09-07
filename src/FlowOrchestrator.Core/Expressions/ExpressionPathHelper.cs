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

        var normalizedPath = path
            .Replace("[", ".", StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
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
    /// The exact-match attempt runs first so a payload that genuinely carries both
    /// <c>"location"</c> and <c>"Location"</c> still resolves to the spelling the
    /// expression asked for.
    /// </remarks>
    private static bool TryGetPropertyRelaxed(JsonElement target, string name, out JsonElement value)
    {
        if (target.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in target.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
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
