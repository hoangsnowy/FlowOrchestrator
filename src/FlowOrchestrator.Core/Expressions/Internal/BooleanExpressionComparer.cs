using System.Text.Json;

namespace FlowOrchestrator.Core.Expressions.Internal;

/// <summary>
/// Type unification + binary comparison helpers used by <see cref="ComparisonNode"/>.
/// Implements the type rules documented on <see cref="BooleanExpressionEvaluator"/>:
/// numbers compare as <see cref="decimal"/>, strings ordinally, bools directly,
/// and any other type combination throws <see cref="FlowExpressionException"/>.
/// </summary>
internal static class BooleanExpressionComparer
{
    /// <summary>
    /// Coerces <paramref name="value"/> to a comparable .NET primitive — most JSON kinds
    /// to their idiomatic .NET equivalent, every numeric type to <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// Marked <see langword="internal"/> rather than <see langword="private"/> because the
    /// <see cref="EvalContext"/> resolver pipeline calls it on every LHS resolution to
    /// pre-unwrap the value before caching.
    /// </remarks>
    internal static object? UnwrapJson(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : (object?)el.GetDouble(),
                JsonValueKind.Object or JsonValueKind.Array => el.GetRawText(),
                _ => null
            };
        }
        return value switch
        {
            byte b => (decimal)b,
            sbyte sb => (decimal)sb,
            short s => (decimal)s,
            ushort us => (decimal)us,
            int i => (decimal)i,
            uint ui => (decimal)ui,
            long l => (decimal)l,
            ulong ul => (decimal)ul,
            float f => (decimal)f,
            double dbl => (decimal)dbl,
            decimal d => d,
            _ => value
        };
    }

    /// <summary>Applies binary comparison <paramref name="op"/> to <paramref name="leftRaw"/> and <paramref name="rightRaw"/>.</summary>
    public static bool Compare(object? leftRaw, object? rightRaw, TokenKind op, string source)
    {
        var left = UnwrapJson(leftRaw);
        var right = UnwrapJson(rightRaw);

        if (left is null || right is null)
        {
            return op switch
            {
                TokenKind.Eq => left is null && right is null,
                TokenKind.Neq => !(left is null && right is null),
                _ => throw new FlowExpressionException(
                    source,
                    stepKey: string.Empty,
                    $"Cannot apply operator '{OpText(op)}' to null operand in expression '{source}'.")
            };
        }

        if (left is decimal ld && right is decimal rd)
        {
            return op switch
            {
                TokenKind.Eq => ld == rd,
                TokenKind.Neq => ld != rd,
                TokenKind.Gt => ld > rd,
                TokenKind.Lt => ld < rd,
                TokenKind.Gte => ld >= rd,
                TokenKind.Lte => ld <= rd,
                _ => throw Unreachable(op, source)
            };
        }

        if (left is string ls && right is string rs)
        {
            var cmp = string.CompareOrdinal(ls, rs);
            return op switch
            {
                TokenKind.Eq => cmp == 0,
                TokenKind.Neq => cmp != 0,
                TokenKind.Gt => cmp > 0,
                TokenKind.Lt => cmp < 0,
                TokenKind.Gte => cmp >= 0,
                TokenKind.Lte => cmp <= 0,
                _ => throw Unreachable(op, source)
            };
        }

        if (left is bool lb && right is bool rb)
        {
            return op switch
            {
                TokenKind.Eq => lb == rb,
                TokenKind.Neq => lb != rb,
                _ => throw new FlowExpressionException(
                    source,
                    stepKey: string.Empty,
                    $"Operator '{OpText(op)}' is not defined for booleans in expression '{source}'.")
            };
        }

        throw new FlowExpressionException(
            source,
            stepKey: string.Empty,
            $"Cannot compare values of types '{FormatType(left)}' and '{FormatType(right)}' with operator '{OpText(op)}' in expression '{source}'.");
    }

    /// <summary>Friendly name of <paramref name="value"/>'s type for error messages.</summary>
    public static string FormatType(object? value) => value switch
    {
        null => "null",
        string => "string",
        bool => "bool",
        decimal => "number",
        _ => value.GetType().Name
    };

    /// <summary>Text representation of a comparison/logical operator for error messages.</summary>
    public static string OpText(TokenKind op) => op switch
    {
        TokenKind.Eq => "==",
        TokenKind.Neq => "!=",
        TokenKind.Gt => ">",
        TokenKind.Lt => "<",
        TokenKind.Gte => ">=",
        TokenKind.Lte => "<=",
        TokenKind.And => "&&",
        TokenKind.Or => "||",
        TokenKind.Not => "!",
        _ => op.ToString()
    };

    private static FlowExpressionException Unreachable(TokenKind op, string source) =>
        new(source, string.Empty, $"Unreachable: operator '{OpText(op)}' was not handled in expression '{source}'.");
}
