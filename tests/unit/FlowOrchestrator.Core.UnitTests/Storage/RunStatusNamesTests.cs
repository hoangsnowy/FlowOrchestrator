using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// <see cref="RunStatusNames.Canonicalize"/> maps a status filter in any casing onto the spelling the engine
/// persists, so a byte-comparing store (PostgreSQL) filters like the case-insensitive ones.
/// </summary>
public sealed class RunStatusNamesTests
{
    [Theory]
    [InlineData("failed", "Failed")]
    [InlineData("FAILED", "Failed")]
    [InlineData(" running ", "Running")]
    [InlineData("succeeded", "Succeeded")]
    [InlineData("skipped", "Skipped")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("timedout", "TimedOut")]
    public void Canonicalize_known_status_in_any_casing_returns_the_persisted_spelling(string requested, string expected)
    {
        // Arrange

        // Act
        var canonical = RunStatusNames.Canonicalize(requested);

        // Assert
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Canonicalize_returns_the_shared_constant_instance()
    {
        // Arrange

        // Act
        var canonical = RunStatusNames.Canonicalize("failed");

        // Assert
        Assert.Same(RunStatusNames.Failed, canonical);
    }

    [Fact]
    public void Canonicalize_unknown_status_is_returned_unchanged()
    {
        // Arrange
        const string custom = "AwaitingReview";

        // Act
        var canonical = RunStatusNames.Canonicalize(custom);

        // Assert
        Assert.Same(custom, canonical);
    }
}
