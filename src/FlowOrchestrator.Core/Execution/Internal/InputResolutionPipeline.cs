using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Synchronous resolution of <c>@triggerBody()</c> and <c>@triggerHeaders()</c> expressions
/// in a step's input dictionary. First pass of the input resolution pipeline used by
/// <see cref="DefaultStepExecutor"/>; runs before the async step-output resolution.
/// </summary>
internal static class InputResolutionPipeline
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns a dictionary with all <c>@triggerBody()</c> / <c>@triggerHeaders()</c> expressions
    /// resolved against the supplied trigger data. Returns the original dictionary unchanged
    /// when no value could possibly need resolution — keeps the steady-state path zero-allocation.
    /// </summary>
    /// <param name="inputs">The raw input dictionary from the step instance.</param>
    /// <param name="triggerData">The trigger payload, if any.</param>
    /// <param name="triggerHeaders">The trigger header map, if any.</param>
    public static IDictionary<string, object?> Resolve(
        IDictionary<string, object?> inputs,
        object? triggerData,
        IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (inputs.Count == 0)
            return inputs;

        // Most steps have purely literal inputs (numbers, plain strings, bools).
        // For those the resolution is a no-op but the original code still
        // allocated a fresh Dictionary copy. Pre-scan the values: if none can
        // possibly be — or contain — a trigger expression, return the original
        // dictionary unchanged. This makes the common case zero-allocation.
        if (!ContainsResolvableValue(inputs))
        {
            return inputs;
        }

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
            resolved[key] = ResolveValue(value, triggerData, triggerHeaders);

        return resolved;
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one value in
    /// <paramref name="inputs"/> could plausibly need expression resolution —
    /// a string starting with <c>@</c>, a <see cref="JsonElement"/>, or a
    /// nested collection. Strings that don't start with <c>@</c> and primitive
    /// values are skipped entirely.
    /// </summary>
    private static bool ContainsResolvableValue(IDictionary<string, object?> inputs)
    {
        foreach (var value in inputs.Values)
        {
            switch (value)
            {
                case string s when StartsWithAt(s):
                    return true;
                case JsonElement:
                case IDictionary<string, object?>:
                case IEnumerable<object?>:
                    // Nested structures may contain expressions — fall through
                    // to the full resolution path.
                    return true;
                default:
                    continue;
            }
        }
        return false;
    }

    /// <summary>
    /// Mirrors the fast-path used by <see cref="TriggerExpressionResolver"/>:
    /// returns <see langword="true"/> when the first non-whitespace character
    /// of <paramref name="value"/> is <c>@</c>. Avoids the full <c>Trim</c>
    /// allocation for the common literal-string case.
    /// </summary>
    private static bool StartsWithAt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            return c == '@';
        }
        return false;
    }

    private static object? ResolveValue(object? value, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                if (TryResolveTriggerBodyExpression(s, triggerData, out var resolvedBody))
                    return resolvedBody;
                if (TryResolveTriggerHeadersExpression(s, triggerHeaders, out var resolvedHeader))
                    return resolvedHeader;
                return s;
            case JsonElement element:
                return ResolveJsonElement(element, triggerData, triggerHeaders);
            case IDictionary<string, object?> dict:
                return Resolve(dict, triggerData, triggerHeaders);
            case IEnumerable<object?> sequence:
                return sequence.Select(item => ResolveValue(item, triggerData, triggerHeaders)).ToArray();
            default:
                return value;
        }
    }

    private static object? ResolveJsonElement(JsonElement element, object? triggerData, IReadOnlyDictionary<string, string>? triggerHeaders)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind == JsonValueKind.String)
        {
            var stringValue = element.GetString();
            if (TryResolveTriggerBodyExpression(stringValue, triggerData, out var resolvedBody))
                return resolvedBody;
            if (TryResolveTriggerHeadersExpression(stringValue, triggerHeaders, out var resolvedHeader))
                return resolvedHeader;
            return CloneOrNull(element);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var objectValue = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), _jsonOptions)
                              ?? new Dictionary<string, object?>();
            return Resolve(objectValue, triggerData, triggerHeaders);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var arrayValue = JsonSerializer.Deserialize<List<object?>>(element.GetRawText(), _jsonOptions) ?? [];
            return arrayValue.Select(item => ResolveValue(item, triggerData, triggerHeaders)).ToArray();
        }

        return CloneOrNull(element);
    }

    private static JsonElement? CloneOrNull(JsonElement element)
    {
        try
        {
            return element.Clone();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Trigger-body / trigger-headers expression resolution lives in the canonical
    // TriggerExpressionResolver. The thin pass-through methods below preserve the
    // historical call sites without re-implementing the logic.
    private static bool TryResolveTriggerBodyExpression(string? expression, object? triggerData, out object? resolved)
        => TriggerExpressionResolver.TryResolveTriggerBodyExpression(expression, triggerData, out resolved);

    private static bool TryResolveTriggerHeadersExpression(string? expression, IReadOnlyDictionary<string, string>? headers, out object? resolved)
        => TriggerExpressionResolver.TryResolveTriggerHeadersExpression(expression, headers, out resolved);
}
