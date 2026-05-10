using System.Text.Json;
using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the steady-state allocation contract of <see cref="InputResolutionPipeline"/>:
/// when no value in the input dictionary can possibly need expression resolution
/// (no <c>@</c>-prefixed strings, no <see cref="JsonElement"/>, no nested collections),
/// the pipeline returns the original dictionary instance unchanged. Reference equality
/// is the test signal — a regression that drops the fast path would silently allocate
/// a fresh dictionary on every step execution.
/// </summary>
public class InputResolutionPipelineTests
{
    [Fact]
    public void Resolve_PrimitiveOnlyInputs_ReturnsSameReference()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>
        {
            ["count"] = 42,
            ["name"] = "Alice",
            ["enabled"] = true,
            ["amount"] = 12.5m
        };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Same(inputs, resolved);
    }

    [Fact]
    public void Resolve_StringWithoutAtPrefix_ReturnsSameReference()
    {
        // Arrange — non-expression strings should not trip the resolution path.
        var inputs = new Dictionary<string, object?>
        {
            ["greeting"] = "hello world"
        };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Same(inputs, resolved);
    }

    [Fact]
    public void Resolve_EmptyInputs_ReturnsSameReference()
    {
        // Arrange
        var inputs = new Dictionary<string, object?>();

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.Same(inputs, resolved);
    }

    [Fact]
    public void Resolve_AtPrefixedString_AllocatesNewDictionary()
    {
        // Arrange — POCO trigger data is serialised to a JsonElement before resolution.
        var inputs = new Dictionary<string, object?>
        {
            ["target"] = "@triggerBody()"
        };
        var triggerData = new { id = 7 };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData, triggerHeaders: null);

        // Assert — fast-path bailed out (new dict) and the expression resolved to non-null.
        Assert.NotSame(inputs, resolved);
        Assert.NotNull(resolved["target"]);
    }

    [Fact]
    public void Resolve_NestedCollection_AllocatesNewDictionary()
    {
        // Arrange — nested collections may contain expressions, so the fast path bails out.
        var inputs = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["k"] = "v" }
        };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData: null, triggerHeaders: null);

        // Assert
        Assert.NotSame(inputs, resolved);
    }

    [Fact]
    public void Resolve_TriggerHeaderExpression_ReturnsHeaderValue()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["X-Request-Id"] = "req-123" };
        var inputs = new Dictionary<string, object?>
        {
            ["requestId"] = "@triggerHeaders()['X-Request-Id']"
        };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData: null, headers);

        // Assert
        Assert.Equal("req-123", resolved["requestId"]);
    }

    [Fact]
    public void Resolve_AtPrefixedStringWithLeadingWhitespace_StillTriggersResolution()
    {
        // Arrange — fast-path scan must skip whitespace before the `@` check.
        var inputs = new Dictionary<string, object?>
        {
            ["target"] = "  @triggerBody()"
        };
        var triggerData = new { id = 1 };

        // Act
        var resolved = InputResolutionPipeline.Resolve(inputs, triggerData, triggerHeaders: null);

        // Assert — value should resolve, meaning fast-path correctly bailed out for the leading-ws case.
        Assert.NotSame(inputs, resolved);
        Assert.NotNull(resolved["target"]);
    }
}
