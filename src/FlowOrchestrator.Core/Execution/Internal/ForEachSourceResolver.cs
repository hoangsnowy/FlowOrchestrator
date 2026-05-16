using System.Text.Json;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Resolves the iteration source value for a <c>ForEach</c> step. Handles three cases:
/// <list type="bullet">
///   <item>A literal collection passed via the static manifest.</item>
///   <item>A <c>@triggerBody()</c> expression bound to the run's trigger payload.</item>
///   <item>A <c>@triggerHeaders()['name']</c> expression bound to the run's HTTP headers.</item>
/// </list>
/// </summary>
/// <remarks>
/// Carries a local copy of the trigger-expression parser (rather than delegating to
/// <see cref="Expressions.TriggerExpressionResolver"/>) because the ForEach path adds a
/// <c>Length &gt;= 4</c> guard for header-key brackets that the canonical resolver does not
/// yet enforce — unifying is a follow-up PR.
/// </remarks>
internal static class ForEachSourceResolver
{
    /// <summary>
    /// Resolves <paramref name="value"/> against the run's trigger context.
    /// Returns <paramref name="value"/> unchanged when it is not an expression string.
    /// </summary>
    public static object? Resolve(object? value, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (value is not string expression)
        {
            return value;
        }

        var resolvedFromBody = TryResolveTriggerBodyExpression(expression, triggerData, out var resolvedBody);
        var resolvedFromHeaders = TryResolveTriggerHeadersExpression(expression, triggerHeaders, out var resolvedHeaders);

        if (!resolvedFromBody && !resolvedFromHeaders)
        {
            return value;
        }

        return resolvedBody ?? resolvedHeaders;
    }

    /// <summary>
    /// Materialises an arbitrary source value into a list of items the handler can iterate.
    /// Returns an empty list for <see langword="null"/> or non-enumerable inputs.
    /// </summary>
    public static List<object?> ToItemList(object? source)
    {
        if (source is null)
        {
            return [];
        }

        if (source is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return element.EnumerateArray()
                .Select(x => (object?)x.Clone())
                .ToList();
        }

        if (source is IEnumerable<object?> objectSequence)
        {
            return objectSequence.ToList();
        }

        if (source is System.Collections.IEnumerable sequence and not string)
        {
            var list = new List<object?>();
            foreach (var item in sequence)
            {
                list.Add(item);
            }
            return list;
        }

        return [];
    }

    /// <summary>
    /// Fast-path check shared by the trigger-body and trigger-headers resolvers:
    /// every <c>@trigger…()</c> token starts with <c>@</c>, possibly preceded by
    /// whitespace. Skip the <see cref="string.Trim()"/> allocation and the prefix
    /// <see cref="string.StartsWith(string, StringComparison)"/> call when the first
    /// non-whitespace character isn't <c>@</c>.
    /// </summary>
    private static bool StartsWithAt(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return false;
        }

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            return c == '@';
        }

        return false;
    }

    private static bool TryResolveTriggerBodyExpression(string? expression, object? triggerData, out object? resolved)
    {
        resolved = null;
        if (!StartsWithAt(expression))
        {
            return false;
        }

        const string prefix = "@triggerBody()";
        var trimmed = expression!.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = trimmed[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            resolved = triggerData;
            return true;
        }

        if (remainder.StartsWith("?.", StringComparison.Ordinal))
        {
            remainder = remainder[2..];
        }
        else if (remainder.StartsWith(".", StringComparison.Ordinal))
        {
            remainder = remainder[1..];
        }
        else
        {
            return false;
        }

        if (triggerData is null)
        {
            resolved = null;
            return true;
        }

        var payload = triggerData is JsonElement jsonElement
            ? jsonElement
            : JsonSerializer.SerializeToElement(triggerData);

        if (TryResolvePath(payload, remainder, out var target))
        {
            resolved = target;
            return true;
        }

        resolved = null;
        return true;
    }

    private static bool TryResolveTriggerHeadersExpression(string? expression, IReadOnlyDictionary<string, string>? headers, out object? resolved)
    {
        resolved = null;
        if (!StartsWithAt(expression))
        {
            return false;
        }

        const string prefix = "@triggerHeaders()";
        var trimmed = expression!.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = trimmed[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            resolved = headers;
            return true;
        }

        string? headerName = null;
        // `['x']` / `["x"]` need at least 4 chars; without this guard, `"[']"` (length 3)
        // satisfies both StartsWith and EndsWith and the `[2..^2]` slice throws.
        if (remainder.Length >= 4 && remainder.StartsWith("['", StringComparison.Ordinal) && remainder.EndsWith("']", StringComparison.Ordinal))
            headerName = remainder[2..^2];
        else if (remainder.Length >= 4 && remainder.StartsWith("[\"", StringComparison.Ordinal) && remainder.EndsWith("\"]", StringComparison.Ordinal))
            headerName = remainder[2..^2];

        if (headerName is not null)
        {
            resolved = headers is not null && headers.TryGetValue(headerName, out var value) ? value : null;
            return true;
        }

        return false;
    }

    private static bool TryResolvePath(JsonElement payload, string path, out JsonElement target)
    {
        target = payload;
        var normalizedPath = path.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var objectValue))
            {
                target = objectValue;
                continue;
            }

            if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < target.GetArrayLength())
            {
                target = target[index];
                continue;
            }

            return false;
        }

        return true;
    }
}
