using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;

namespace FlowOrchestrator.InMemory.Tests;

/// <summary>
/// Covers the two <see cref="InMemoryFlowRunStore"/> changes from #189: the run-id text match no longer
/// formats <c>run.Id.ToString()</c> per candidate run but must match exactly what it matched before, and
/// the new <see cref="InMemoryFlowRunStore.IsStepDispatchedAsync"/> point lookup must agree with
/// <see cref="InMemoryFlowRunStore.GetDispatchedStepKeysAsync"/>.
/// </summary>
public sealed class InMemoryFlowRunStoreIssue189Tests
{
    private readonly InMemoryFlowRunStore _sut = new();

    private static readonly Guid KnownId = Guid.Parse("3f2b8c1d-7a4e-4b9f-8c21-0d5e6f7a8b9c");

    [Theory]
    [InlineData("3f2b8c1d")]
    [InlineData("7A4E-4B9F")]
    [InlineData("1d-7a4e-4b9f-8c21-0d5e6f7a8b9")]
    [InlineData("8b9c")]
    public async Task IdFragment_MatchesOnBothSearchEntryPoints_CaseInsensitively(string fragment)
    {
        // Arrange
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", KnownId, "manual", null, null);
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", Guid.Parse("00000000-0000-0000-0000-000000000001"), "manual", null, null);

        // Act
        var (legacyRuns, legacyTotal) = await _sut.GetRunsPageAsync(null, null, 0, 20, fragment, deepSearch: false);
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = fragment });

        // Assert
        Assert.Equal(1, legacyTotal);
        Assert.Equal(KnownId, Assert.Single(legacyRuns).Id);
        Assert.Equal(KnownId, Assert.Single(page.Runs).Id);
    }

    [Theory]
    [InlineData("3f2b8c1d-7a4e-4b9f-8c21-0d5e6f7a8b9c-extra")]
    [InlineData("3f2b8c1d7a4e")]
    [InlineData("zz")]
    public async Task NonMatchingTerms_DoNotMatchTheId(string term)
    {
        // Arrange
        await _sut.StartRunAsync(Guid.NewGuid(), "Flow", KnownId, "manual", null, null);

        // Act
        var (legacyRuns, legacyTotal) = await _sut.GetRunsPageAsync(null, null, 0, 20, term, deepSearch: false);
        var page = await _sut.SearchRunsAsync(new RunSearchQuery { Text = term });

        // Assert
        Assert.Equal(0, legacyTotal);
        Assert.Empty(legacyRuns);
        Assert.Empty(page.Runs);
    }

    [Fact]
    public async Task IsStepDispatchedAsync_TracksRecordAndRelease()
    {
        // Arrange
        var runId = Guid.NewGuid();
        await _sut.TryRecordDispatchAsync(runId, "loop.3.child");

        // Act
        var whileDispatched = await _sut.IsStepDispatchedAsync(runId, "loop.3.child");
        var otherKey = await _sut.IsStepDispatchedAsync(runId, "loop.4.child");
        var otherRun = await _sut.IsStepDispatchedAsync(Guid.NewGuid(), "loop.3.child");
        await _sut.ReleaseDispatchAsync(runId, "loop.3.child");
        var afterRelease = await _sut.IsStepDispatchedAsync(runId, "loop.3.child");

        // Assert
        Assert.True(whileDispatched);
        Assert.False(otherKey);
        Assert.False(otherRun);
        Assert.False(afterRelease);
    }

    [Fact]
    public async Task IsStepDispatchedAsync_AgreesWithTheFullLedgerRead()
    {
        // Arrange
        var runId = Guid.NewGuid();
        var keys = Enumerable.Range(0, 20).Select(i => $"loop.{i}.child").ToArray();
        foreach (var key in keys)
        {
            await _sut.TryRecordDispatchAsync(runId, key);
        }

        await _sut.ReleaseDispatchAsync(runId, keys[7]);

        // Act
        var ledger = await _sut.GetDispatchedStepKeysAsync(runId);
        var pointAnswers = new List<bool>();
        foreach (var key in keys)
        {
            pointAnswers.Add(await _sut.IsStepDispatchedAsync(runId, key));
        }

        // Assert
        Assert.Equal(keys.Select(ledger.Contains), pointAnswers);
    }
}
