using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// <see cref="RuntimeStepKey.TryGetInnermostScope"/> is the span walk the continuation's loop advance uses to
/// find the loop a changed row belongs to; it must agree with the first scope
/// <see cref="RuntimeStepKey.EnumerateScopes"/> yields for every key, regular or not.
/// </summary>
public sealed class RuntimeStepKeyInnermostScopeTests
{
    [Theory]
    [InlineData("step")]
    [InlineData("loop.3.child")]
    [InlineData("loop.3")]
    [InlineData("outer.1.inner.0.child")]
    [InlineData("outer.1.inner.12")]
    [InlineData("a.b.c")]
    [InlineData("loop.-1.child")]
    [InlineData("loop.007.child")]
    [InlineData("loop..3.child")]
    [InlineData("loop. 3 .child")]
    [InlineData(".loop.3")]
    [InlineData("loop.3.")]
    [InlineData("")]
    public void AgreesWithEnumerateScopes(string key)
    {
        // Arrange
        var expected = RuntimeStepKey.EnumerateScopes(key).Take(1).ToList();

        // Act
        var found = RuntimeStepKey.TryGetInnermostScope(key, out var scope);

        // Assert
        Assert.Equal(expected.Count == 1, found);
        if (found)
        {
            Assert.Equal(expected[0], scope);
        }
    }

    [Fact]
    public void NestedKey_yieldsTheInnermostLoopAndItsIndex()
    {
        // Arrange
        const string key = "outer.1.inner.4.child";

        // Act
        var found = RuntimeStepKey.TryGetInnermostScope(key, out var scope);

        // Assert
        Assert.True(found);
        Assert.Equal(new RuntimeScope("outer.1.inner", 4), scope);
    }
}
