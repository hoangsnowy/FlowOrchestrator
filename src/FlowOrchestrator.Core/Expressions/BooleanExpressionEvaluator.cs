using System.Text.Json;
using FlowOrchestrator.Core.Expressions.Internal;

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
/// The implementation is split across four internal files for readability:
/// <see cref="BooleanExpressionLexer"/> (lex), <see cref="BooleanExpressionParser"/> (parse),
/// <c>BooleanExpressionAst.cs</c> (AST + <see cref="EvalContext"/>), and
/// <see cref="BooleanExpressionComparer"/> (type unification + binary comparison).
/// </para>
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

        var tokens = BooleanExpressionLexer.Tokenize(expression);
        var parser = new BooleanExpressionParser(tokens, expression);
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
}
