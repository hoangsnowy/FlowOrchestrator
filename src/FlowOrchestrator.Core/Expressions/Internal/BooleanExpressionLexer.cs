using System.Text;

namespace FlowOrchestrator.Core.Expressions.Internal;

/// <summary>
/// Token kinds produced by <see cref="BooleanExpressionLexer.Tokenize"/>.
/// </summary>
internal enum TokenKind
{
    LParen, RParen,
    Eq, Neq, Gt, Lt, Gte, Lte,
    And, Or, Not,
    Number, String, True, False, Null,
    Lhs,
    End
}

/// <summary>
/// Lexer token: a kind, the raw source text, and the position in the source string
/// where the token begins. Used for parser error messages.
/// </summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Position);

/// <summary>
/// First stage of the <see cref="BooleanExpressionEvaluator"/> compiler pipeline.
/// Scans the raw expression text and produces a flat <see cref="List{T}"/> of <see cref="Token"/>s
/// terminated by a <see cref="TokenKind.End"/> sentinel.
/// </summary>
internal static class BooleanExpressionLexer
{
    /// <summary>Tokenises <paramref name="source"/>; throws <see cref="FlowExpressionException"/> on lex errors.</summary>
    public static List<Token> Tokenize(string source)
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
}
