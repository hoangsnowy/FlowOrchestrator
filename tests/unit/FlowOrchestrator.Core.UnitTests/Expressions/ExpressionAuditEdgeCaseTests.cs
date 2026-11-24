using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Locks down the semantics chosen for the 2026-11-24 engine-audit expression fixes beyond the
/// auditor's "does not throw" repros: numbers outside the <see cref="decimal"/> range compare
/// correctly as <see cref="double"/>, malformed literals stay authoring errors, and the
/// null-conditional is honoured anywhere in a path, including before an index.
/// </summary>
public sealed class ExpressionAuditEdgeCaseTests
{
    private static readonly JsonElement Huge = JsonDocument.Parse("1e300").RootElement.Clone();

    private static ValueTask<object?> ResolveHuge(string _) => new(Huge);

    [Theory]
    [InlineData("@x > 5", true)]
    [InlineData("@x < 5", false)]
    [InlineData("@x == 5", false)]
    [InlineData("@x != 5", true)]
    [InlineData("-5 < @x", true)]
    public async Task Evaluate_LhsOutsideDecimalRange_ComparesAsDouble(string expression, bool expected)
    {
        // Arrange
        var evaluator = new BooleanExpressionEvaluator();

        // Act
        var trace = await evaluator.EvaluateAsync(expression, ResolveHuge);

        // Assert
        Assert.Equal(expected, trace.Result);
    }

    [Fact]
    public async Task Evaluate_LiteralOutsideDecimalRange_ComparesAsDouble()
    {
        // Arrange
        var evaluator = new BooleanExpressionEvaluator();
        const string expression = "999999999999999999999999999999999999 > 79228162514264337593543950335";

        // Act
        var trace = await evaluator.EvaluateAsync(expression, _ => new ValueTask<object?>((object?)null));

        // Assert
        Assert.True(trace.Result);
    }

    [Fact]
    public async Task Evaluate_InRangeNumbers_StillCompareAsExactDecimal()
    {
        // Arrange — 0.1 + 0.2 style precision: decimal keeps 0.3 exact where double would not.
        var evaluator = new BooleanExpressionEvaluator();
        var value = JsonDocument.Parse("0.3").RootElement.Clone();

        // Act
        var trace = await evaluator.EvaluateAsync("@x == 0.3", _ => new ValueTask<object?>(value));

        // Assert
        Assert.True(trace.Result);
    }

    [Theory]
    [InlineData("1.2.3 == 1")]
    [InlineData("@x > 1..0")]
    public async Task Evaluate_MalformedNumberLiteral_ThrowsFlowExpressionExceptionWithSource(string expression)
    {
        // Arrange
        var evaluator = new BooleanExpressionEvaluator();

        // Act
        var ex = await Assert.ThrowsAsync<FlowExpressionException>(
            async () => await evaluator.EvaluateAsync(expression, ResolveHuge));

        // Assert
        Assert.Equal(expression, ex.Expression);
        Assert.Contains("Invalid number literal", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@triggerBody()?.order?.lines?[1]?.sku", "B")]
    [InlineData("@triggerBody().order?.lines[0].sku", "A")]
    [InlineData("@triggerBody()?.order.lines?[0]?.sku", "A")]
    public void TriggerBody_NullConditionalAnywhere_ResolvesValue(string expression, string expected)
    {
        // Arrange
        var body = JsonSerializer.SerializeToElement(new { order = new { lines = new[] { new { sku = "A" }, new { sku = "B" } } } });

        // Act
        var matched = TriggerExpressionResolver.TryResolveTriggerBodyExpression(expression, body, out var resolved);

        // Assert
        Assert.True(matched);
        Assert.Equal(expected, Assert.IsType<JsonElement>(resolved).GetString());
    }

    [Fact]
    public void TriggerBody_NullConditionalOverNullMember_ResolvesNull()
    {
        // Arrange
        var body = JsonSerializer.SerializeToElement(new { order = (object?)null });

        // Act
        var matched = TriggerExpressionResolver.TryResolveTriggerBodyExpression("@triggerBody()?.order?.id", body, out var resolved);

        // Assert
        Assert.True(matched);
        Assert.Null(resolved);
    }
}
