using System.Globalization;

namespace FlowOrchestrator.Core.Expressions.Internal;

/// <summary>
/// Per-evaluation context: holds the LHS resolver delegate, caches resolutions so a
/// repeated LHS is only resolved once, and tracks the resolution order for the
/// <see cref="WhenEvaluationTrace.Resolved"/> rewrite string.
/// </summary>
internal sealed class EvalContext
{
    private readonly BooleanExpressionEvaluator.LhsResolverAsync _resolver;
    public string Expression { get; }
    private readonly Dictionary<string, object?> _resolutions = new(StringComparer.Ordinal);
    private readonly List<(string Lhs, string Replacement)> _resolutionOrder = new();

    public EvalContext(BooleanExpressionEvaluator.LhsResolverAsync resolver, string expression)
    {
        _resolver = resolver;
        Expression = expression;
    }

    /// <summary>Resolves a LHS token to a comparable value, caching subsequent calls.</summary>
    public async ValueTask<object?> ResolveAsync(string lhs)
    {
        if (_resolutions.TryGetValue(lhs, out var cached))
        {
            return cached;
        }

        var raw = await _resolver(lhs).ConfigureAwait(false);
        var unwrapped = BooleanExpressionComparer.UnwrapJson(raw);
        _resolutions[lhs] = unwrapped;
        _resolutionOrder.Add((lhs, FormatForTrace(unwrapped)));
        return unwrapped;
    }

    /// <summary>Builds the rewrite of <see cref="Expression"/> with each LHS replaced by its formatted value.</summary>
    public string BuildResolvedRewrite()
    {
        var rewrite = Expression;
        foreach (var (lhs, replacement) in _resolutionOrder)
        {
            rewrite = ReplaceFirst(rewrite, lhs, replacement);
        }
        return rewrite;
    }

    private static string ReplaceFirst(string source, string search, string replacement)
    {
        var idx = source.IndexOf(search, StringComparison.Ordinal);
        return idx < 0 ? source : source[..idx] + replacement + source[(idx + search.Length)..];
    }

    private static string FormatForTrace(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };
}

/// <summary>AST node interface — every expression node is asynchronously evaluable against an <see cref="EvalContext"/>.</summary>
internal interface IExprNode
{
    ValueTask<object?> EvaluateAsync(EvalContext ctx);
}

/// <summary>Constant literal: number / string / bool / null.</summary>
internal sealed class LiteralNode : IExprNode
{
    private readonly object? _value;
    public LiteralNode(object? value) { _value = value; }
    public ValueTask<object?> EvaluateAsync(EvalContext _) => new(_value);
}

/// <summary>LHS reference: <c>@steps('x')…</c> or trigger/body/header expressions.</summary>
internal sealed class LhsNode : IExprNode
{
    public string Lhs { get; }
    public LhsNode(string lhs) { Lhs = lhs; }
    public ValueTask<object?> EvaluateAsync(EvalContext ctx) => ctx.ResolveAsync(Lhs);
}

/// <summary>Logical NOT — requires a boolean operand.</summary>
internal sealed class NotNode : IExprNode
{
    private readonly IExprNode _inner;
    public NotNode(IExprNode inner) { _inner = inner; }
    public async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var v = await _inner.EvaluateAsync(ctx).ConfigureAwait(false);
        if (v is not bool b)
        {
            throw new FlowExpressionException(
                ctx.Expression,
                stepKey: string.Empty,
                $"Operator '!' requires a boolean operand; got '{BooleanExpressionComparer.FormatType(v)}' in expression '{ctx.Expression}'.");
        }
        return !b;
    }
}

/// <summary>Logical AND with short-circuit on <see langword="false"/> LHS.</summary>
internal sealed class AndNode : IExprNode
{
    private readonly IExprNode _left, _right;
    public AndNode(IExprNode l, IExprNode r) { _left = l; _right = r; }
    public async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var leftVal = await _left.EvaluateAsync(ctx).ConfigureAwait(false);
        if (leftVal is not bool lb)
        {
            throw new FlowExpressionException(
                ctx.Expression,
                stepKey: string.Empty,
                $"Operator '&&' requires boolean operands; left was '{BooleanExpressionComparer.FormatType(leftVal)}' in expression '{ctx.Expression}'.");
        }
        if (!lb) return false; // short-circuit
        var rightVal = await _right.EvaluateAsync(ctx).ConfigureAwait(false);
        if (rightVal is not bool rb)
        {
            throw new FlowExpressionException(
                ctx.Expression,
                stepKey: string.Empty,
                $"Operator '&&' requires boolean operands; right was '{BooleanExpressionComparer.FormatType(rightVal)}' in expression '{ctx.Expression}'.");
        }
        return rb;
    }
}

/// <summary>Logical OR with short-circuit on <see langword="true"/> LHS.</summary>
internal sealed class OrNode : IExprNode
{
    private readonly IExprNode _left, _right;
    public OrNode(IExprNode l, IExprNode r) { _left = l; _right = r; }
    public async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var leftVal = await _left.EvaluateAsync(ctx).ConfigureAwait(false);
        if (leftVal is not bool lb)
        {
            throw new FlowExpressionException(
                ctx.Expression,
                stepKey: string.Empty,
                $"Operator '||' requires boolean operands; left was '{BooleanExpressionComparer.FormatType(leftVal)}' in expression '{ctx.Expression}'.");
        }
        if (lb) return true; // short-circuit
        var rightVal = await _right.EvaluateAsync(ctx).ConfigureAwait(false);
        if (rightVal is not bool rb)
        {
            throw new FlowExpressionException(
                ctx.Expression,
                stepKey: string.Empty,
                $"Operator '||' requires boolean operands; right was '{BooleanExpressionComparer.FormatType(rightVal)}' in expression '{ctx.Expression}'.");
        }
        return rb;
    }
}

/// <summary>
/// Binary comparison: equality / ordering. Type rules are enforced by
/// <see cref="BooleanExpressionComparer.Compare"/>.
/// </summary>
internal sealed class ComparisonNode : IExprNode
{
    private readonly IExprNode _left, _right;
    private readonly TokenKind _op;
    private readonly string _source;

    public ComparisonNode(IExprNode l, TokenKind op, IExprNode r, string source)
    {
        _left = l; _op = op; _right = r; _source = source;
    }

    public async ValueTask<object?> EvaluateAsync(EvalContext ctx)
    {
        var leftRaw = await _left.EvaluateAsync(ctx).ConfigureAwait(false);
        var rightRaw = await _right.EvaluateAsync(ctx).ConfigureAwait(false);
        return BooleanExpressionComparer.Compare(leftRaw, rightRaw, _op, _source);
    }
}
