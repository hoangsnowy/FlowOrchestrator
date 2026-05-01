using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Expressions;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Tests.Planning;

/// <summary>
/// The 13 invariants required by Plan 05 (When-clause). These exercise the boolean
/// evaluator end-to-end against the same resolver shape the engine uses, and verify
/// JSON roundtripping plus backwards-compat with the legacy <c>StepStatus[]</c> shape.
/// </summary>
public class RunAfterConditionTests
{
    private readonly BooleanExpressionEvaluator _evaluator = new();

    private static BooleanExpressionEvaluator.LhsResolverAsync ResolverFor(IDictionary<string, object?> map)
        => lhs => new ValueTask<object?>(map.TryGetValue(lhs, out var v) ? v : null);

    // ── 1) Equality with string, number, bool, null ─────────────────────────

    [Theory]
    [InlineData("@v == 'approved'", "approved", true)]
    [InlineData("@v == 'approved'", "rejected", false)]
    public async Task EqualityString(string expr, string value, bool expected)
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = value });

        // Act
        var trace = await _evaluator.EvaluateAsync(expr, resolver);

        // Assert
        Assert.Equal(expected, trace.Result);
    }

    [Fact]
    public async Task EqualityNumberBoolNull_All()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@n"] = 1500m,
            ["@b"] = true,
            ["@nl"] = null
        });

        // Act
        var n  = await _evaluator.EvaluateAsync("@n == 1500", resolver);
        var b  = await _evaluator.EvaluateAsync("@b == true", resolver);
        var nl = await _evaluator.EvaluateAsync("@nl == null", resolver);

        // Assert
        Assert.True(n.Result);
        Assert.True(b.Result);
        Assert.True(nl.Result);
    }

    // ── 2) All 6 comparison operators ────────────────────────────────────────

    [Theory]
    [InlineData("@v == 5", true)]
    [InlineData("@v != 5", false)]
    [InlineData("@v > 4", true)]
    [InlineData("@v < 6", true)]
    [InlineData("@v >= 5", true)]
    [InlineData("@v <= 5", true)]
    public async Task AllComparisonOperators(string expr, bool expected)
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@v"] = 5m });

        // Act
        var trace = await _evaluator.EvaluateAsync(expr, resolver);

        // Assert
        Assert.Equal(expected, trace.Result);
    }

    // ── 3) && short-circuits ────────────────────────────────────────────────

    [Fact]
    public async Task AndShortCircuit_RhsNotEvaluated()
    {
        // Arrange
        var calls = 0;
        BooleanExpressionEvaluator.LhsResolverAsync resolver = lhs =>
        {
            if (lhs == "@right") { calls++; return new ValueTask<object?>(true); }
            return new ValueTask<object?>(false);
        };

        // Act
        var trace = await _evaluator.EvaluateAsync("@left && @right", resolver);

        // Assert
        Assert.False(trace.Result);
        Assert.Equal(0, calls);
    }

    // ── 4) || short-circuits ────────────────────────────────────────────────

    [Fact]
    public async Task OrShortCircuit_RhsNotEvaluated()
    {
        // Arrange
        var calls = 0;
        BooleanExpressionEvaluator.LhsResolverAsync resolver = lhs =>
        {
            if (lhs == "@right") { calls++; return new ValueTask<object?>(false); }
            return new ValueTask<object?>(true);
        };

        // Act
        var trace = await _evaluator.EvaluateAsync("@left || @right", resolver);

        // Assert
        Assert.True(trace.Result);
        Assert.Equal(0, calls);
    }

    // ── 5) Parenthesised expressions ────────────────────────────────────────

    [Fact]
    public async Task ParenthesisedExpression()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@a"] = 6m,
            ["@b"] = 1m,
            ["@c"] = "ok"
        });

        // Act
        var trace = await _evaluator.EvaluateAsync("(@a > 5 || @b > 10) && @c == 'ok'", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── 6) Negation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Negation()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.flag"] = true });

        // Act
        var trace = await _evaluator.EvaluateAsync("!@steps('x').output.flag", resolver);

        // Assert
        Assert.False(trace.Result);
    }

    // ── 7) Status passes but When false → Skipped (signal: trace.Result = false) ──

    [Fact]
    public async Task StatusPassesButWhenFalse_TraceFalse()
    {
        // Arrange
        var condition = new RunAfterCondition
        {
            Statuses = [StepStatus.Succeeded],
            When = "@steps('fetch').output.amount > 1000"
        };
        Assert.True(condition.AcceptsStatus(StepStatus.Succeeded));
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@steps('fetch').output.amount"] = 500m
        });

        // Act
        var trace = await _evaluator.EvaluateAsync(condition.When!, resolver);

        // Assert
        Assert.False(trace.Result);
    }

    // ── 8) Status passes and When true → step runs ──────────────────────────

    [Fact]
    public async Task StatusPassesAndWhenTrue_TraceTrue()
    {
        // Arrange
        var condition = new RunAfterCondition
        {
            Statuses = [StepStatus.Succeeded],
            When = "@steps('fetch').output.amount > 1000"
        };
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@steps('fetch').output.amount"] = 1500m
        });

        // Act
        var trace = await _evaluator.EvaluateAsync(condition.When!, resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── 9) When references @steps('x').output.path ──────────────────────────

    [Fact]
    public async Task WhenReferencesStepsOutputPath()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@steps('fetch').output.customer.region"] = "EU"
        });

        // Act
        var trace = await _evaluator.EvaluateAsync("@steps('fetch').output.customer.region == 'EU'", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── 10) When references @triggerBody() ──────────────────────────────────

    [Fact]
    public async Task WhenReferencesTriggerBody()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@triggerBody().priority"] = "high"
        });

        // Act
        var trace = await _evaluator.EvaluateAsync("@triggerBody().priority == 'high'", resolver);

        // Assert
        Assert.True(trace.Result);
    }

    // ── 11) Type-coercion error throws FlowExpressionException ──────────────

    [Fact]
    public async Task TypeCoercionError_HelpfulMessage()
    {
        // Arrange
        var resolver = ResolverFor(new Dictionary<string, object?> { ["@steps('x').output.amount"] = "1000" });

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await _evaluator.EvaluateAsync("@steps('x').output.amount > 500", resolver));

        // Assert
        Assert.Contains("@steps('x').output.amount > 500", ex.Message);
        Assert.Contains("string", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    // ── 12) Backwards compat: existing array-of-statuses syntax unchanged ──

    [Fact]
    public void LegacyArraySyntax_StillCompilesAndBehaves()
    {
        // Arrange + Act
        var collection = new RunAfterCollection
        {
            ["validate"] = [StepStatus.Succeeded, StepStatus.Skipped]
        };
        var entry = collection["validate"];

        // Assert
        Assert.NotNull(entry.Statuses);
        Assert.Equal(2, entry.Statuses!.Length);
        Assert.True(entry.AcceptsStatus(StepStatus.Succeeded));
        Assert.True(entry.AcceptsStatus(StepStatus.Skipped));
        Assert.False(entry.AcceptsStatus(StepStatus.Failed));
        Assert.Null(entry.When);
    }

    // ── 13) Roundtrip: serialize manifest to JSON, deserialize, re-evaluate ──

    [Fact]
    public async Task Roundtrip_ManifestSerializeDeserializeReevaluate()
    {
        // Arrange — build a manifest with both legacy + new shapes.
        var original = new StepCollection
        {
            ["fetch"] = new StepMetadata { Type = "Fetch" },
            ["legacy"] = new StepMetadata
            {
                Type = "Log",
                RunAfter = new RunAfterCollection { ["fetch"] = [StepStatus.Succeeded] }
            },
            ["modern"] = new StepMetadata
            {
                Type = "Log",
                RunAfter = new RunAfterCollection
                {
                    ["fetch"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@steps('fetch').output.amount > 1000"
                    }
                }
            }
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Act
        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<StepCollection>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        var legacy = deserialized!["legacy"].RunAfter["fetch"];
        Assert.NotNull(legacy.Statuses);
        Assert.Single(legacy.Statuses!);
        Assert.Equal(StepStatus.Succeeded, legacy.Statuses![0]);
        Assert.Null(legacy.When);

        var modern = deserialized["modern"].RunAfter["fetch"];
        Assert.NotNull(modern.Statuses);
        Assert.Equal("@steps('fetch').output.amount > 1000", modern.When);

        // Re-evaluating the deserialised When clause produces the same result
        var resolver = ResolverFor(new Dictionary<string, object?>
        {
            ["@steps('fetch').output.amount"] = 1500m
        });
        var trace = await _evaluator.EvaluateAsync(modern.When!, resolver);
        Assert.True(trace.Result);
    }
}
