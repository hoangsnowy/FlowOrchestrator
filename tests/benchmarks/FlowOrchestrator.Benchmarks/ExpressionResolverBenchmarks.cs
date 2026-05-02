using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Benchmarks;

/// <summary>
/// Measures the hot-path cost of expression resolution helpers used during
/// every step execution. Three input shapes exercised:
///   <list type="bullet">
///     <item>A string literal that is NOT an expression (the most common shape — fast-paths).</item>
///     <item>A trigger-body expression with a single path segment.</item>
///     <item>A step-output expression that returns from the parse cache after the first call.</item>
///   </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class ExpressionResolverBenchmarks
{
    private const string LiteralInput = "ProductionOrderId-12345";
    private const string TriggerBodyExpr = "@triggerBody().orderId";
    private const string StepOutputExpr = "@steps('fetch').output.amount";

    private static readonly object _triggerData = new
    {
        orderId = "ORD-9001",
        amount = 42.5m,
        nested = new { a = 1, b = "two" }
    };

    private static readonly IReadOnlyDictionary<string, string> _triggerHeaders =
        new Dictionary<string, string>
        {
            ["X-Request-Id"] = "req-1",
            ["Content-Type"] = "application/json",
        };

    /// <summary>The plain-literal input is by far the most common — measures pure overhead.</summary>
    [Benchmark(Baseline = true, Description = "Literal input — fast-path rejection")]
    public bool LiteralReject()
    {
        var hit = TriggerExpressionResolver.TryResolveTriggerBodyExpression(LiteralInput, _triggerData, out _);
        return hit;
    }

    /// <summary>Hot path: a real trigger-body resolution that reaches the JsonElement walker.</summary>
    [Benchmark(Description = "@triggerBody().orderId resolution")]
    public object? TriggerBodyResolve()
    {
        TriggerExpressionResolver.TryResolveTriggerBodyExpression(TriggerBodyExpr, _triggerData, out var resolved);
        return resolved;
    }

    /// <summary>
    /// Static cache hit on <see cref="StepOutputResolver.IsStepExpression(string)"/> followed by
    /// the parse-cache hit. The async resolution itself is exercised in a separate benchmark
    /// because it requires a live store.
    /// </summary>
    [Benchmark(Description = "IsStepExpression + parse-cache lookup")]
    public bool StepExpressionGate()
    {
        return StepOutputResolver.IsStepExpression(StepOutputExpr);
    }

    /// <summary>
    /// Counterpoint to the gate: same call against a literal — must also fast-path return.
    /// </summary>
    [Benchmark(Description = "IsStepExpression on literal (fast-path)")]
    public bool StepExpressionGateLiteral()
    {
        return StepOutputResolver.IsStepExpression(LiteralInput);
    }

    /// <summary>Headers fast-path on a literal.</summary>
    [Benchmark(Description = "TryResolveTriggerHeadersExpression literal")]
    public bool HeadersLiteral()
    {
        return TriggerExpressionResolver.TryResolveTriggerHeadersExpression(LiteralInput, _triggerHeaders, out _);
    }

    /// <summary>Headers happy path — bracketed lookup.</summary>
    [Benchmark(Description = "@triggerHeaders()['X-Request-Id']")]
    public object? HeadersResolve()
    {
        TriggerExpressionResolver.TryResolveTriggerHeadersExpression(
            "@triggerHeaders()['X-Request-Id']",
            _triggerHeaders,
            out var resolved);
        return resolved;
    }
}
