using FlowOrchestrator.Core.Execution.Internal;

namespace FlowOrchestrator.Core.Tests.Execution.Internal;

/// <summary>
/// Locks the contract <see cref="RunTerminationClassifier"/> and <c>FlowGraphPlanner</c> rely on:
/// a <see cref="StepKeySlice"/> hashes and compares as the logical key it spells, whatever its shape.
/// </summary>
public class StepKeySliceTests
{
    [Theory]
    [InlineData("loop.3.child", "loop.3.other", 6, "child")]
    [InlineData("loop.3", "loop.3.child", 6, null)]
    [InlineData("child", "child.x", 5, null)]
    [InlineData("outer.0.inner.1.leaf", "outer.0.inner.1.tail", 15, "leaf")]
    public void DifferentShapes_SpellingTheSameKey_AreEqualAndHashEqually(string whole, string head, int headLength, string? tail)
    {
        // Arrange
        var comparer = StepKeySliceComparer.Instance;
        var a = StepKeySlice.Of(whole);
        var b = new StepKeySlice(head, headLength, tail);

        // Act
        var equal = comparer.Equals(a, b);
        var sameHash = comparer.GetHashCode(a) == comparer.GetHashCode(b);

        // Assert
        Assert.True(equal);
        Assert.True(sameHash);
        Assert.Equal(whole, b.ToString());
    }

    [Theory]
    [InlineData("loop.3.child", "loop.3.x", 6, "chile")]
    [InlineData("loop.3.child", "loop.30.child", 7, "child")]
    [InlineData("loop.3", "loop.3.child", 4, null)]
    [InlineData("Loop.3", "loop.3", 6, null)]
    public void DifferentKeys_AreNotEqual(string whole, string head, int headLength, string? tail)
    {
        // Arrange
        var comparer = StepKeySliceComparer.Instance;

        // Act
        var equal = comparer.Equals(StepKeySlice.Of(whole), new StepKeySlice(head, headLength, tail));

        // Assert
        Assert.False(equal);
    }

    [Fact]
    public void HashSet_FindsAConcatenationSliceByAWholeKeySlice()
    {
        // Arrange
        var set = new HashSet<StepKeySlice>(StepKeySliceComparer.Instance)
        {
            new("loop.12.child_1", 7, "child_0")
        };

        // Act
        var found = set.Contains(StepKeySlice.Of("loop.12.child_0"));

        // Assert
        Assert.True(found);
    }
}
