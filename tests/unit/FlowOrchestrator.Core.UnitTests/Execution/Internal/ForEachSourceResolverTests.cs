using System.Text.Json;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the boundary behaviours of <see cref="ForEachSourceResolver"/> that the
/// canonical <c>TriggerExpressionResolver</c> does NOT yet share — specifically the
/// header-bracket length guard. These tests document why the local copy still exists
/// and protect against accidental unification regressions.
/// </summary>
public class ForEachSourceResolverTests
{
    [Fact]
    public void Resolve_LiteralCollection_ReturnsValueUnchanged()
    {
        // Arrange
        var literal = new[] { 1, 2, 3 };

        // Act
        var resolved = ForEachSourceResolver.Resolve(literal, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Same(literal, resolved);
    }

    [Fact]
    public void Resolve_TriggerBodyExpression_ReturnsTriggerData()
    {
        // Arrange
        var triggerData = new[] { "a", "b" };

        // Act
        var resolved = ForEachSourceResolver.Resolve("@triggerBody()", triggerData, triggerHeaders: null);

        // Assert
        Assert.Same(triggerData, resolved);
    }

    [Fact]
    public void Resolve_NonExpressionString_ReturnsStringUnchanged()
    {
        // Arrange
        const string literal = "not an expression";

        // Act
        var resolved = ForEachSourceResolver.Resolve(literal, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Equal(literal, resolved);
    }

    [Fact]
    public void Resolve_HeaderExpressionWithLengthThreeBrackets_DoesNotThrow()
    {
        // Arrange — `@triggerHeaders()[']` has remainder length 3; the canonical resolver
        // would throw on `[2..^2]`. The local guard at `Length >= 4` is the documented
        // divergence and the reason this resolver is not yet unified upstream.
        var headers = new Dictionary<string, string> { ["X-Test"] = "value" };

        // Act + Assert
        var resolved = ForEachSourceResolver.Resolve("@triggerHeaders()[']", triggerData: null, headers);
        Assert.Equal("@triggerHeaders()[']", resolved);
    }

    [Fact]
    public void Resolve_HeaderExpressionWithSingleQuotedKey_ReturnsHeaderValue()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["X-Custom"] = "matched" };

        // Act
        var resolved = ForEachSourceResolver.Resolve("@triggerHeaders()['X-Custom']", triggerData: null, headers);

        // Assert
        Assert.Equal("matched", resolved);
    }

    [Fact]
    public void ToItemList_NullSource_ReturnsEmpty()
    {
        // Arrange + Act
        var items = ForEachSourceResolver.ToItemList(null);

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public void ToItemList_JsonArray_ReturnsClonedItems()
    {
        // Arrange
        using var doc = JsonDocument.Parse("[1, 2, 3]");
        var element = doc.RootElement;

        // Act
        var items = ForEachSourceResolver.ToItemList(element);

        // Assert
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ToItemList_StringSource_ReturnsEmpty()
    {
        // Arrange — string is IEnumerable but excluded by `not string` guard so a literal
        // string is not iterated character-by-character.
        const string source = "abc";

        // Act
        var items = ForEachSourceResolver.ToItemList(source);

        // Assert
        Assert.Empty(items);
    }
}
