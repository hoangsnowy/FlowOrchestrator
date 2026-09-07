using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Evaluates whether a JSON payload satisfies a polling condition expressed as a dot-notation
/// path and an optional expected value. Used by <c>PollableStepHandler</c> to decide whether
/// the external system response meets the completion criteria.
/// </summary>
internal static class PollConditionEvaluator
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="payload"/> satisfies the condition.
    /// </summary>
    /// <param name="payload">The JSON document returned by the polled endpoint.</param>
    /// <param name="conditionPath">
    /// Dot-notation path to the target field (e.g. <c>status.code</c>). Pass <see langword="null"/>
    /// or empty to test whether the root payload has any data.
    /// </param>
    /// <param name="expectedValue">
    /// Value the resolved field must equal (case-insensitive string comparison). Pass
    /// <see langword="null"/> to treat any non-empty value as a match.
    /// </param>
    public static bool IsMatched(JsonElement payload, string? conditionPath, object? expectedValue)
    {
        if (!TryResolvePath(payload, conditionPath, out var target))
        {
            return false;
        }

        if (expectedValue is null)
        {
            return HasData(target);
        }

        return string.Equals(Normalize(target), Normalize(expectedValue), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks <paramref name="path"/> into <paramref name="payload"/>, treating an empty path as
    /// "the payload itself".
    /// </summary>
    /// <remarks>
    /// Navigation is delegated to <see cref="ExpressionPathHelper"/> so a poll condition resolves
    /// with the same semantics as every other expression path in a manifest. This carried a third
    /// private copy of the walker, which was still matching property names case-sensitively: a
    /// C#-authored <c>conditionPath</c> of <c>"Status.Code"</c> never matched a response persisted
    /// as <c>{"status":{"code":…}}</c>, so the condition silently stayed false and the step polled
    /// until its timeout instead of completing — the same failure mode as issue #177.
    /// </remarks>
    private static bool TryResolvePath(JsonElement payload, string? path, out JsonElement target)
    {
        target = payload;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return ExpressionPathHelper.TryResolvePath(payload, path, out target);
    }

    private static bool HasData(JsonElement target) => target.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(target.GetString()),
        JsonValueKind.Array => target.GetArrayLength() > 0,
        JsonValueKind.Object => target.EnumerateObject().MoveNext(),
        _ => true
    };

    private static string Normalize(object? value) => value switch
    {
        null => string.Empty,
        JsonElement json => Normalize(json),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => value.GetRawText()
    };
}
