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
    internal static bool TryResolvePath(JsonElement payload, string path, out JsonElement target)
    {
        target = payload;

        var normalizedPath = path
            .Replace("[", ".", StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var prop))
            {
                target = prop;
                continue;
            }

            if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var idx))
            {
                if (idx >= 0 && idx < target.GetArrayLength())
                {
                    target = target[idx];
                    continue;
                }
            }

            return false;
        }

        return true;
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
