namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Unit coverage for <see cref="RunStatusDisplay.ResolveSearchAlias"/> — the seam that lets a
/// run search by the dashboard's display label ("Blocked") match the canonical status token.
/// </summary>
public sealed class RunStatusDisplayTests
{
    [Theory]
    [InlineData("blocked")]
    [InlineData("Blocked")]
    [InlineData("BLOCKED")]
    [InlineData("  blocked  ")]
    public void ResolveSearchAlias_BlockedDisplayLabel_MapsToSkipped(string term)
    {
        // Arrange

        // Act
        var resolved = RunStatusDisplay.ResolveSearchAlias(term);

        // Assert
        Assert.Equal("Skipped", resolved);
    }

    [Theory]
    [InlineData("Failed", "Failed")]
    [InlineData("order-flow", "order-flow")]
    [InlineData("block", "block")]
    public void ResolveSearchAlias_NonAliasTerm_ReturnsOriginal(string term, string expected)
    {
        // Arrange

        // Act
        var resolved = RunStatusDisplay.ResolveSearchAlias(term);

        // Assert
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSearchAlias_NullOrWhitespace_ReturnsUnchanged(string? term)
    {
        // Arrange

        // Act
        var resolved = RunStatusDisplay.ResolveSearchAlias(term);

        // Assert
        Assert.Equal(term, resolved);
    }
}
