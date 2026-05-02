using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Boundary-input coverage for the <see cref="StepOutputResolver.IsStepExpression"/>
/// fast path added in v1.20.0. The fast path skips leading whitespace and
/// returns <see langword="false"/> early when the first non-whitespace character
/// is not <c>@</c> or when there is insufficient room for the <c>@steps(</c>
/// prefix. These tests pin every documented boundary so future refactors of
/// the fast path can't silently regress on edge cases.
/// </summary>
public sealed class StepOutputResolverFastPathTests
{
    [Fact]
    public void IsStepExpression_NullInput_ReturnsFalse()
    {
        // Arrange
        string? input = null;

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsStepExpression_EmptyString_ReturnsFalse()
    {
        // Arrange
        var input = string.Empty;

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsStepExpression_WhitespaceOnly_ReturnsFalse()
    {
        // Arrange
        var input = "   \t  ";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsStepExpression_BareAtSign_ReturnsFalse()
    {
        // Arrange — single '@' is shorter than "@steps(" (7 chars), should fail length floor.
        var input = "@";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsStepExpression_SixCharNonMatchingPrefix_ReturnsFalse()
    {
        // Arrange — "@step(" is 6 chars, below the 7-char minimum for "@steps(".
        var input = "@step(";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsStepExpression_ExactSevenCharPrefix_ReturnsTrue()
    {
        // Arrange — exactly the prefix, nothing after. The gate accepts; full
        // resolution would later fail to extract a key, but the gate is intentionally
        // shape-only so it stays cheap on the hot path.
        var input = "@steps(";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsStepExpression_LeadingWhitespaceBeforeAt_ReturnsTrue()
    {
        // Arrange
        var input = "   @steps('fetch').output";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsStepExpression_UppercasePrefix_IsCaseInsensitive()
    {
        // Arrange — the resolver pattern uses RegexOptions.IgnoreCase; the gate
        // must match.
        var input = "@STEPS('fetch').output";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsStepExpression_NonAtFirstChar_RejectsImmediately()
    {
        // Arrange — most inputs in practice are plain literals; this is the
        // common-case fast-path.
        var input = "ProductionOrderId-12345";

        // Act
        var result = StepOutputResolver.IsStepExpression(input);

        // Assert
        Assert.False(result);
    }
}
