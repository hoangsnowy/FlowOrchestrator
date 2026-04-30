using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Tests.Expressions;

public class BooleanExpressionEvaluatorTests
{
    private readonly BooleanExpressionEvaluator _sut = new();

    private static BooleanExpressionEvaluator.LhsResolverAsync ResolverFor(IDictionary<string, object?> map)
        => lhs => new ValueTask<object?>(map.TryGetValue(lhs, out var v) ? v : null);

    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    // ── Equality across types ────────────────────────────────────────────────

    [Fact]
    public async Task Equality_String_True()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.status"] = "approved" });

        // Act
        var trace = await _sut.EvaluateAsync("@steps('x').output.status == 'approved'", resolver);

        // Assert
        Assert.True(trace.Result);
        Assert.Equal("'approved' == 'approved'", trace.Resolved);
    }

    [Fact]
    public async Task Equality_Number_True()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.amount"] = 1500m });

        // Act
        var trace = await _sut.EvaluateAsync("@steps('x').output.amount == 1500", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    [Fact]
    public async Task Equality_Bool_True()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@triggerBody().flag"] = true });

        // Act
        var trace = await _sut.EvaluateAsync("@triggerBody().flag == true", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    [Fact]
    public async Task Equality_NullEqualsNull_True()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@triggerBody()?.missing"] = null });

        // Act
        var trace = await _sut.EvaluateAsync("@triggerBody()?.missing == null", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    [Fact]
    public async Task Inequality_NullVsValue_True()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@triggerBody()?.region"] = "US" });

        // Act
        var trace = await _sut.EvaluateAsync("@triggerBody()?.region != null", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── Comparison operators ─────────────────────────────────────────────────

    [Theory]
    [InlineData("@v > 5", 10, true)]
    [InlineData("@v > 5", 5, false)]
    [InlineData("@v < 5", 4, true)]
    [InlineData("@v < 5", 5, false)]
    [InlineData("@v >= 5", 5, true)]
    [InlineData("@v >= 5", 4, false)]
    [InlineData("@v <= 5", 5, true)]
    [InlineData("@v <= 5", 6, false)]
    public async Task ComparisonOperators_NumericMatrix(string expression, int value, bool expected)
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = (decimal)value });

        // Act
        var trace = await _sut.EvaluateAsync(expression, resolver);

        // Assert
        Assert.Equal(expected, trace.Result);
    }

    // ── Logical AND short-circuit ────────────────────────────────────────────

    [Fact]
    public async Task And_ShortCircuit_DoesNotResolveRightWhenLeftFalse()
    {
        // Arrange
        var rightCalls = 0;
        BooleanExpressionEvaluator.LhsResolverAsync resolver = lhs =>
        {
            if (lhs == "@right")
            {
                rightCalls++;
                return new ValueTask<object?>(true);
            }
            return new ValueTask<object?>(false);
        };

        // Act
        var trace = await _sut.EvaluateAsync("@left && @right", resolver);

        // Assert
        Assert.False(trace.Result);
        Assert.Equal(0, rightCalls);
    }

    // ── Logical OR short-circuit ─────────────────────────────────────────────

    [Fact]
    public async Task Or_ShortCircuit_DoesNotResolveRightWhenLeftTrue()
    {
        // Arrange
        var rightCalls = 0;
        BooleanExpressionEvaluator.LhsResolverAsync resolver = lhs =>
        {
            if (lhs == "@right") { rightCalls++; return new ValueTask<object?>(false); }
            return new ValueTask<object?>(true);
        };

        // Act
        var trace = await _sut.EvaluateAsync("@left || @right", resolver);

        // Assert
        Assert.True(trace.Result);
        Assert.Equal(0, rightCalls);
    }

    // ── Parentheses ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Parentheses_PrecedenceOverride()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@a"] = 6m,   // satisfies a > 5
            ["@b"] = 1m,   // does not satisfy b > 10
            ["@c"] = "ok"  // satisfies c == 'ok'
        });

        // Act
        var trace = await _sut.EvaluateAsync("(@a > 5 || @b > 10) && @c == 'ok'", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── Negation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Negation_FlipsBoolean()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.flag"] = true });

        // Act
        var trace = await _sut.EvaluateAsync("!@steps('x').output.flag", resolver);

        // Assert
        Assert.False(trace.Result);
    }

    // ── Type coercion errors ─────────────────────────────────────────────────

    [Fact]
    public async Task TypeCoercion_StringVsNumber_Throws()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = "100" });

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await _sut.EvaluateAsync("@v > 50", resolver));

        // Assert
        Assert.Contains("@v > 50", ex.Message);
        Assert.Contains("string", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    [Fact]
    public async Task NullComparison_OrderingOperator_Throws()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = null });

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await _sut.EvaluateAsync("@v > 5", resolver));

        // Assert
        Assert.Contains("null", ex.Message);
    }

    // ── JsonElement unwrapping ───────────────────────────────────────────────

    [Fact]
    public async Task JsonElement_NumberIsUnwrappedAsDecimal()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.amount"] = Json("1500") });

        // Act
        var trace = await _sut.EvaluateAsync("@steps('x').output.amount > 1000", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── Resolved trace rewrite ───────────────────────────────────────────────

    [Fact]
    public async Task Resolved_RewritesEachLhsWithItsValue()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@steps('fetch').output.amount"] = 500m
        });

        // Act
        var trace = await _sut.EvaluateAsync("@steps('fetch').output.amount > 1000", resolver);

        // Assert
        Assert.False(trace.Result);
        Assert.Equal("500 > 1000", trace.Resolved);
    }

    [Fact]
    public async Task NonBooleanResult_Throws()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = 42m });

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await _sut.EvaluateAsync("@v", resolver));

        // Assert
        Assert.Contains("must evaluate to a boolean", ex.Message);
    }
}
