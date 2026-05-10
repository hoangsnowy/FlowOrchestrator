using System.Globalization;

namespace FlowOrchestrator.Core.Expressions.Internal;

/// <summary>
/// Hand-rolled recursive-descent parser. Second stage of the
/// <see cref="BooleanExpressionEvaluator"/> compiler pipeline — consumes the
/// <see cref="Token"/> stream produced by <see cref="BooleanExpressionLexer"/>
/// and returns an <see cref="IExprNode"/> AST root.
/// </summary>
/// <remarks>
/// Grammar precedence: <c>||</c> &lt; <c>&amp;&amp;</c> &lt; <c>!</c> &lt; comparison &lt; primary.
/// </remarks>
internal sealed class BooleanExpressionParser
{
    private readonly List<Token> _tokens;
    private readonly string _source;
    private int _index;

    /// <summary>Initialises the parser with the lexed token stream and the raw source for error reporting.</summary>
    public BooleanExpressionParser(List<Token> tokens, string source)
    {
        _tokens = tokens;
        _source = source;
    }

    private Token Peek() => _tokens[_index];
    private Token Consume() => _tokens[_index++];

    /// <summary>Throws if any non-<see cref="TokenKind.End"/> token remains after the parsed expression.</summary>
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

    /// <summary>Parses the whole expression and returns the AST root.</summary>
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
