using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Hand-rolled recursive-descent parser and evaluator for the limited boolean expression
/// grammar supported by the <c>When</c> clause on <see cref="Abstractions.RunAfterCondition"/>.
/// </summary>
/// <remarks>
/// <para>Grammar (informal):</para>
/// <code>
/// expr        = orExpr
/// orExpr      = andExpr ("||" andExpr)*
/// andExpr     = notExpr ("&amp;&amp;" notExpr)*
/// notExpr     = "!" notExpr | comparison
/// comparison  = primary (("==" | "!=" | "&gt;" | "&lt;" | "&gt;=" | "&lt;=") primary)?
/// primary     = "(" expr ")" | literal | lhsExpression
/// literal     = number | string | "true" | "false" | "null"
/// lhsExpression = "@steps(...)" | "@triggerBody(...)" | "@triggerHeaders(...)"
/// </code>
/// <para>
/// Type rules: number-vs-number compares as <see cref="decimal"/>; string-vs-string compares
/// ordinally; bool-vs-bool compares directly; <see langword="null"/> is only equal/non-equal
/// to <see langword="null"/>; any other type combination throws <see cref="FlowExpressionException"/>.
/// </para>
/// <para>
/// Short-circuit semantics: <c>&amp;&amp;</c> does not evaluate the RHS when the LHS is
/// <see langword="false"/>; <c>||</c> does not evaluate the RHS when the LHS is <see langword="true"/>.
/// </para>
/// </remarks>
public sealed class BooleanExpressionEvaluator
{
    /// <summary>
    /// Resolves a left-hand-side expression token (e.g. <c>@steps('x').output.amount</c>) to a
    /// comparable value. Implementations typically delegate to <see cref="StepOutputResolver"/>
    /// and the trigger-body/headers helpers in <c>DefaultStepExecutor</c>.
    /// </summary>
    /// <param name="lhsExpression">The raw LHS token, including the leading <c>@</c>.</param>
    /// <returns>
    /// A <see cref="JsonElement"/>, primitive value, or <see langword="null"/>. <see cref="JsonElement"/>
    /// values are unwrapped to comparable .NET values inside the evaluator.
    /// </returns>
    public delegate ValueTask<object?> LhsResolverAsync(string lhsExpression);

    /// <summary>
    /// Parses and evaluates <paramref name="expression"/>, returning a trace that captures
    /// both the boolean result and a human-readable rewrite with each LHS replaced by its
    /// resolved value.
    /// </summary>
    /// <param name="expression">The expression text from the manifest.</param>
    /// <param name="resolver">A resolver invoked once per unique LHS token encountered during evaluation.</param>
    /// <exception cref="FlowExpressionException">Thrown on parse errors or type-coercion failures.</exception>
    public async ValueTask<WhenEvaluationTrace> EvaluateAsync(string expression, LhsResolverAsync resolver)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(resolver);

        var tokens = Tokenize(expression);
        var parser = new Parser(tokens, expression);
        var ast = parser.ParseExpression();
        parser.ExpectEnd();

        var ctx = new EvalContext(resolver, expression);
        var resultValue = await ast.EvaluateAsync(ctx).ConfigureAwait(false);

        if (resultValue is not bool b)
        {
            throw new FlowExpressionException(
                expression,
                stepKey: string.Empty,
                $"Expression must evaluate to a boolean. Expression: '{expression}'.");
        }

        return new WhenEvaluationTrace
        {
            Expression = expression,
            Resolved = ctx.BuildResolvedRewrite(),
            Result = b
        };
    }

    // ───────────────────────────── Tokenizer ─────────────────────────────

    private enum TokenKind
    {
        LParen, RParen,
        Eq, Neq, Gt, Lt, Gte, Lte,
        And, Or, Not,
        Number, String, True, False, Null,
        Lhs,
        End
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(", i)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")", i)); i++; continue; }

            if (c == '=' && i + 1 < source.Length && source[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.Eq, "==", i)); i += 2; continue; }

            if (c == '!' && i + 1 < source.Length && source[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.Neq, "!=", i)); i += 2; continue; }

            if (c == '!')
            { tokens.Add(new Token(TokenKind.Not, "!", i)); i++; continue; }

            if (c == '>' && i + 1 < source.Length && source[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.Gte, ">=", i)); i += 2; continue; }
            if (c == '<' && i + 1 < source.Length && source[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.Lte, "<=", i)); i += 2; continue; }
            if (c == '>') { tokens.Add(new Token(TokenKind.Gt, ">", i)); i++; continue; }
            if (c == '<') { tokens.Add(new Token(TokenKind.Lt, "<", i)); i++; continue; }

            if (c == '&' && i + 1 < source.Length && source[i + 1] == '&')
            { tokens.Add(new Token(TokenKind.And, "&&", i)); i += 2; continue; }

            if (c == '|' && i + 1 < source.Length && source[i + 1] == '|')
            { tokens.Add(new Token(TokenKind.Or, "||", i)); i += 2; continue; }

            if (c == '\'' || c == '"')
            {
                var (text, len) = ReadStringLiteral(source, i, c);
                tokens.Add(new Token(TokenKind.String, text, i));
                i += len;
                continue;
            }

            if (c == '@')
            {
                var (text, len) = ReadLhsExpression(source, i);
                tokens.Add(new Token(TokenKind.Lhs, text, i));
                i += len;
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                var (text, len) = ReadNumberLiteral(source, i);
                tokens.Add(new Token(TokenKind.Number, text, i));
                i += len;
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                var word = source.AsSpan(start, i - start).ToString();
                var kind = word.ToLowerInvariant() switch
                {
                    "true" => TokenKind.True,
                    "false" => TokenKind.False,
                    "null" => TokenKind.Null,
                    _ => throw new FlowExpressionException(
                        source,
                        stepKey: string.Empty,
                        $"Unexpected identifier '{word}' at position {start}. Only 'true', 'false', 'null' literals are allowed; reference values via @steps()/@triggerBody()/@triggerHeaders().")
                };
                tokens.Add(new Token(kind, word, start));
                continue;
            }

            throw new FlowExpressionException(
                source,
                stepKey: string.Empty,
                $"Unexpected character '{c}' at position {i} in expression '{source}'.");
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }

    private static (string Text, int Length) ReadStringLiteral(string source, int start, char quote)
    {
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length)
            {
                sb.Append(source[i + 1]);
                i += 2;
                continue;
            }
            if (c == quote)
            {
                return (sb.ToString(), i - start + 1);
            }
            sb.Append(c);
            i++;
        }

        throw new FlowExpressionException(
            source,
            stepKey: string.Empty,
            $"Unterminated string literal at position {start} in expression '{source}'.");
    }

    private static (string Text, int Length) ReadNumberLiteral(string source, int start)
    {
        var i = start;
        if (source[i] == '-') i++;
        while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
        return (source.Substring(start, i - start), i - start);
    }

    private static (string Text, int Length) ReadLhsExpression(string source, int start)
    {
        // Scan @...  Stop at top-level whitespace or a top-level operator boundary.
        // Inside parentheses or brackets, all characters belong to the LHS.
        var i = start;
        var parenDepth = 0;
        var bracketDepth = 0;
        while (i < source.Length)
        {
            var c = source[i];

            if (c == '(') { parenDepth++; i++; continue; }
            if (c == ')')
            {
                if (parenDepth == 0) break;
                parenDepth--;
                i++;
                continue;
            }
            if (c == '[') { bracketDepth++; i++; continue; }
            if (c == ']')
            {
                if (bracketDepth == 0) break;
                bracketDepth--;
                i++;
                continue;
            }
            if (parenDepth > 0 || bracketDepth > 0) { i++; continue; }

            if (char.IsWhiteSpace(c)) break;
            if (c is '=' or '!' or '<' or '>' or '&' or '|') break;

            i++;
        }

        return (source.Substring(start, i - start), i - start);
    }

    // ───────────────────────────── Parser ─────────────────────────────

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly string _source;
        private int _index;

        public Parser(List<Token> tokens, string source)
        {
            _tokens = tokens;
            _source = source;
        }

        private Token Peek() => _tokens[_index];
        private Token Consume() => _tokens[_index++];

        public void ExpectEnd()
        {
            if (Peek().Kind != TokenKind.End)
            {
                throw new FlowExpressionException(
                    _source,
                    stepKey: string.Empty,
                    $"Unexpected token '{Peek().Text}' at position {Peek().Position} in expression '{_source}'.");
            }
        }

        public IExprNode ParseExpression() => ParseOr();

        private IExprNode ParseOr()
        {
            var left = ParseAnd();
            while (Peek().Kind == TokenKind.Or)
            {
                Consume();
                var right = ParseAnd();
                left = new OrNode(left, right);
            }
            return left;
        }

        private IExprNode ParseAnd()
        {
            var left = ParseNot();
            while (Peek().Kind == TokenKind.And)
            {
                Consume();
                var right = ParseNot();
                left = new AndNode(left, right);
            }
            return left;
        }

        private IExprNode ParseNot()
        {
            if (Peek().Kind == TokenKind.Not)
            {
                Consume();
                return new NotNode(ParseNot());
            }
            return ParseComparison();
        }

        private IExprNode ParseComparison()
        {
            var left = ParsePrimary();
            var nextKind = Peek().Kind;
            if (nextKind is TokenKind.Eq or TokenKind.Neq or TokenKind.Gt or TokenKind.Lt or TokenKind.Gte or TokenKind.Lte)
            {
                var op = Consume().Kind;
                var right = ParsePrimary();
                return new ComparisonNode(left, op, right, _source);
            }
            return left;
        }

        private IExprNode ParsePrimary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.LParen:
                {
                    Consume();
                    var inner = ParseOr();
                    if (Peek().Kind != TokenKind.RParen)
                    {
                        throw new FlowExpressionException(
                            _source,
                            stepKey: string.Empty,
                            $"Expected ')' at position {Peek().Position} in expression '{_source}'.");
                    }
                    Consume();
                    return inner;
                }
                case TokenKind.Number:
                    Consume();
                    return new LiteralNode(decimal.Parse(t.Text, CultureInfo.InvariantCulture));
                case TokenKind.String:
                    Consume();
                    return new LiteralNode(t.Text);
                case TokenKind.True:
                    Consume();
                    return new LiteralNode(true);
                case TokenKind.False:
                    Consume();
                    return new LiteralNode(false);
                case TokenKind.Null:
                    Consume();
                    return new LiteralNode(null);
                case TokenKind.Lhs:
                    Consume();
                    return new LhsNode(t.Text);
                default:
                    throw new FlowExpressionException(
                        _source,
                        stepKey: string.Empty,
                        $"Unexpected token '{t.Text}' at position {t.Position} in expression '{_source}'.");
            }
        }
    }

    // ───────────────────────────── AST + Evaluation ─────────────────────────────

    private sealed class EvalContext
    {
        private readonly LhsResolverAsync _resolver;
        public string Expression { get; }
        private readonly Dictionary<string, object?> _resolutions = new(StringComparer.Ordinal);
        private readonly List<(string Lhs, string Replacement)> _resolutionOrder = new();

        public EvalContext(LhsResolverAsync resolver, string expression)
        {
            _resolver = resolver;
            Expression = expression;
        }

        public async ValueTask<object?> ResolveAsync(string lhs)
        {
            if (_resolutions.TryGetValue(lhs, out var cached))
            {
                return cached;
            }

            var raw = await _resolver(lhs).ConfigureAwait(false);
            var unwrapped = UnwrapJson(raw);
            _resolutions[lhs] = unwrapped;
            _resolutionOrder.Add((lhs, FormatForTrace(unwrapped)));
            return unwrapped;
        }

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

    private interface IExprNode
    {
        ValueTask<object?> EvaluateAsync(EvalContext ctx);
    }

    private sealed class LiteralNode : IExprNode
    {
        private readonly object? _value;
        public LiteralNode(object? value) { _value = value; }
        public ValueTask<object?> EvaluateAsync(EvalContext _) => new(_value);
    }

    private sealed class LhsNode : IExprNode
    {
        public string Lhs { get; }
        public LhsNode(string lhs) { Lhs = lhs; }
        public ValueTask<object?> EvaluateAsync(EvalContext ctx) => ctx.ResolveAsync(Lhs);
    }

    private sealed class NotNode : IExprNode
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
                    $"Operator '!' requires a boolean operand; got '{FormatType(v)}' in expression '{ctx.Expression}'.");
            }
            return !b;
        }
    }

    private sealed class AndNode : IExprNode
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
                    $"Operator '&&' requires boolean operands; left was '{FormatType(leftVal)}' in expression '{ctx.Expression}'.");
            }
            if (!lb) return false; // short-circuit
            var rightVal = await _right.EvaluateAsync(ctx).ConfigureAwait(false);
            if (rightVal is not bool rb)
            {
                throw new FlowExpressionException(
                    ctx.Expression,
                    stepKey: string.Empty,
                    $"Operator '&&' requires boolean operands; right was '{FormatType(rightVal)}' in expression '{ctx.Expression}'.");
            }
            return rb;
        }
    }

    private sealed class OrNode : IExprNode
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
                    $"Operator '||' requires boolean operands; left was '{FormatType(leftVal)}' in expression '{ctx.Expression}'.");
            }
            if (lb) return true; // short-circuit
            var rightVal = await _right.EvaluateAsync(ctx).ConfigureAwait(false);
            if (rightVal is not bool rb)
            {
                throw new FlowExpressionException(
                    ctx.Expression,
                    stepKey: string.Empty,
                    $"Operator '||' requires boolean operands; right was '{FormatType(rightVal)}' in expression '{ctx.Expression}'.");
            }
            return rb;
        }
    }

    private sealed class ComparisonNode : IExprNode
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
            return Compare(leftRaw, rightRaw, _op, _source);
        }
    }

    // ───────────────────────────── Comparison + helpers ─────────────────────────────

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

    private static bool Compare(object? leftRaw, object? rightRaw, TokenKind op, string source)
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

    private static string FormatType(object? value) => value switch
    {
        null => "null",
        string => "string",
        bool => "bool",
        decimal => "number",
        _ => value.GetType().Name
    };

    private static string OpText(TokenKind op) => op switch
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
