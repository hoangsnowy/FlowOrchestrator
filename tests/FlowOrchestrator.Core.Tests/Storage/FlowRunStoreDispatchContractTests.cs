using FlowOrchestrator.InMemory;
using FluentAssertions;

namespace FlowOrchestrator.Core.Tests.Storage;

/// <summary>
/// Tests for the dispatch-ledger contract of <see cref="InMemoryFlowRunStore"/>:
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.TryRecordDispatchAsync"/>,
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.ReleaseDispatchAsync"/>, and
/// <see cref="FlowOrchestrator.Core.Storage.IFlowRunStore.GetDispatchedStepKeysAsync"/>.
/// These methods form the idempotent dispatch ledger — "Dispatch many, Execute once".
/// </summary>
public sealed class FlowRunStoreDispatchContractTests
{
    // ── Invariant: first record returns true ──────────────────────────────────

    [Fact]
    public async Task TryRecordDispatchAsync_FirstCallForStep_ReturnsTrue()
    {
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        var result = await store.TryRecordDispatchAsync(runId, "step1");

        result.Should().BeTrue("the first dispatch for a (RunId, StepKey) pair must always succeed");
    }

    // ── Invariant: second record for same step returns false ──────────────────

    [Fact]
    public async Task TryRecordDispatchAsync_SecondCallSameStep_ReturnsFalse()
    {
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        await store.TryRecordDispatchAsync(runId, "step1");
        var second = await store.TryRecordDispatchAsync(runId, "step1");

        second.Should().BeFalse(
            "the same (RunId, StepKey) must not be dispatched twice — " +
            "the second call indicates the step is already in the runtime queue");
    }

    // ── Invariant: release allows re-dispatch (polling pattern) ──────────────

    [Fact]
    public async Task ReleaseDispatchAsync_AfterRelease_AllowsReRecording()
    {
        // The Pending (polling) path: engine calls Release before scheduling the next attempt.
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        await store.TryRecordDispatchAsync(runId, "step1");          // initial dispatch
        await store.ReleaseDispatchAsync(runId, "step1");             // engine releases for re-poll
        var canDispatchAgain = await store.TryRecordDispatchAsync(runId, "step1");  // next attempt

        canDispatchAgain.Should().BeTrue(
            "ReleaseDispatchAsync must remove the ledger entry so the step can be re-dispatched " +
            "for the next polling attempt without a false-duplicate guard");
    }

    // ── Invariant: GetDispatchedStepKeysAsync reflects current ledger state ───

    [Fact]
    public async Task GetDispatchedStepKeysAsync_ReflectsDispatchedAndReleasedSteps()
    {
        // Record three dispatches; release one (simulating a Pending step mid-poll).
        var store = new InMemoryFlowRunStore();
        var runId = Guid.NewGuid();

        await store.TryRecordDispatchAsync(runId, "step1");
        await store.TryRecordDispatchAsync(runId, "step2");
        await store.TryRecordDispatchAsync(runId, "step3");
        await store.ReleaseDispatchAsync(runId, "step2");  // step2 released for re-polling

        var keys = await store.GetDispatchedStepKeysAsync(runId);

        keys.Should().BeEquivalentTo(["step1", "step3"],
            "only non-released dispatches should appear; step2 was released and must be absent");
        keys.Should().NotContain("step2",
            "a released dispatch record is not considered 'in flight' and must not block recovery");
    }
}
