using System.Text.Json;

namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Helpers shared between <c>DefaultStepExecutor</c> and <see cref="BooleanExpressionEvaluator"/>
/// for resolving <c>@triggerBody()</c> and <c>@triggerHeaders()</c> expressions to their
/// runtime values.
/// </summary>
internal static class TriggerExpressionResolver
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fast-path check: every <c>@triggerBody()</c> / <c>@triggerHeaders()</c> token starts
    /// with <c>@</c>, possibly preceded by whitespace. Skip the trim allocation and
    /// the prefix <see cref="string.StartsWith(string, StringComparison)"/> call when the
    /// first non-whitespace character isn't <c>@</c>.
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

    /// <summary>
    /// Resolves an <c>@triggerBody()</c> expression (with optional <c>.path</c> trail) against
    /// <paramref name="triggerData"/>.
    /// </summary>
    /// <returns><see langword="true"/> if <paramref name="expression"/> is recognised as a triggerBody reference; the resolved value is then placed in <paramref name="resolved"/>.</returns>
    public static bool TryResolveTriggerBodyExpression(string? expression, object? triggerData, out object? resolved)
    {
        resolved = null;
        if (!StartsWithAt(expression))
        {
            return false;
        }

        const string prefix = "@triggerBody()";
        var trimmed = expression!.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = trimmed[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(remainder))
        {
            resolved = triggerData is null ? null : ExpressionPathHelper.ToJsonElement(triggerData, _jsonOptions);
            return true;
        }

        if (remainder.StartsWith("?.", StringComparison.Ordinal))
            remainder = remainder[2..];
        else if (remainder.StartsWith(".", StringComparison.Ordinal))
            remainder = remainder[1..];
        else
            return false;

        if (string.IsNullOrWhiteSpace(remainder) || triggerData is null)
        {
            resolved = null;
            return true;
        }

        var payload = ExpressionPathHelper.ToJsonElement(triggerData, _jsonOptions);
        if (ExpressionPathHelper.TryResolvePath(payload, remainder, out var target))
        {
            resolved = target;
            return true;
        }

        resolved = null;
        return true;
    }

    /// <summary>
    /// Resolves an <c>@triggerHeaders()</c> expression (with optional bracketed header name) against
    /// <paramref name="headers"/>.
    /// </summary>
    /// <returns><see langword="true"/> if <paramref name="expression"/> is recognised as a triggerHeaders reference; the resolved value is then placed in <paramref name="resolved"/>.</returns>
    public static bool TryResolveTriggerHeadersExpression(string? expression, IReadOnlyDictionary<string, string>? headers, out object? resolved)
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
            resolved = headers is null ? null : ExpressionPathHelper.ToJsonElement(headers, _jsonOptions);
            return true;
        }

        string? headerName = null;
        if (remainder.StartsWith("['", StringComparison.Ordinal) && remainder.EndsWith("']", StringComparison.Ordinal))
            headerName = remainder[2..^2];
        else if (remainder.StartsWith("[\"", StringComparison.Ordinal) && remainder.EndsWith("\"]", StringComparison.Ordinal))
            headerName = remainder[2..^2];

        if (headerName is not null)
        {
            resolved = headers is not null && headers.TryGetValue(headerName, out var val) ? val : null;
            return true;
        }

        return false;
    }
}
