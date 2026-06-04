using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Tests.Expressions;

/// <summary>
/// Regression coverage for the malformed-bracket guard in
/// <see cref="TriggerExpressionResolver.TryResolveTriggerHeadersExpression"/> (finding C8).
/// A header accessor such as <c>@triggerHeaders()[']</c> has a remainder (<c>"[']"</c>) whose
/// length is 3, so the shared middle quote satisfies both the opening and closing bracket
/// tests; without a <c>Length &gt;= 4</c> guard the <c>[2..^2]</c> slice degenerates to
/// <c>[2..1]</c> and throws <see cref="System.ArgumentOutOfRangeException"/>. The resolver must
/// instead treat the token as an unrecognised header accessor (clean unresolved result), exactly
/// as the sibling <c>ForEachSourceResolver</c> already does.
/// </summary>
public class TriggerExpressionResolverBracketTests
{
    private static IReadOnlyDictionary<string, string> Headers() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Foo"] = "bar"
        };

    [Theory]
    [InlineData("@triggerHeaders()[']")]
    [InlineData("@triggerHeaders()[\"]")]
    public void MalformedSingleQuoteBracket_DoesNotThrow_AndReturnsUnresolved(string expression)
    {
        // Arrange
        var headers = Headers();

        // Act — the malformed bracket must not be recognised as a header name.
        var recognised = TriggerExpressionResolver.TryResolveTriggerHeadersExpression(expression, headers, out var resolved);

        // Assert — token is not a valid header accessor, so it is left unrecognised with no value.
        Assert.False(recognised);
        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("@triggerHeaders()[")]
    [InlineData("@triggerHeaders()[]")]
    [InlineData("@triggerHeaders()['']")]
    [InlineData("@triggerHeaders()[\"\"]")]
    public void DegenerateBrackets_DoNotThrow(string expression)
    {
        // Arrange
        var headers = Headers();

        // Act — empty / truncated bracket forms must be slice-safe (no exception thrown).
        TriggerExpressionResolver.TryResolveTriggerHeadersExpression(expression, headers, out var resolved);

        // Assert — `['']` / `[""]` look up an empty header name (absent ⇒ null); truncated
        //          `[` / `[]` are unrecognised (null). Either way the value is null and no throw.
        Assert.Null(resolved);
    }

    [Fact]
    public void WellFormedBracket_StillResolvesHeaderValue()
    {
        // Arrange — guards must not regress the happy path.
        var headers = Headers();

        // Act
        var recognised = TriggerExpressionResolver.TryResolveTriggerHeadersExpression("@triggerHeaders()['X-Foo']", headers, out var resolved);

        // Assert
        Assert.True(recognised);
        Assert.Equal("bar", resolved);
    }
}
