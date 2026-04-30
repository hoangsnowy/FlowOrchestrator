using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Testing.Internal;

/// <summary>
/// <see cref="IFlowRunRuntimeStore"/> wrapper used by <see cref="FlowTestHostBuilder{TFlow}.WithFastPolling"/>.
/// Forwards every method to the inner store except <see cref="TryClaimStepAsync"/>, which always returns
/// <see langword="true"/> so polling reschedules can re-dispatch the same step.
/// </summary>
/// <remarks>
/// The v2 in-memory runtime acquires a per-step claim during <c>TryScheduleStepAsync</c> but never
/// releases it after a <see cref="StepStatus.Pending"/> result, which prevents pollable handlers
/// from rescheduling themselves. Single-worker test runs do not need claim exclusion, so we relax it here.
/// </remarks>
internal sealed class PermissiveRuntimeStore : IFlowRunRuntimeStore
{
    private readonly IFlowRunRuntimeStore _inner;

    public PermissiveRuntimeStore(IFlowRunRuntimeStore inner)
    {
        _inner = inner;
    }

    public Task<IReadOnlyDictionary<string, StepStatus>> GetStepStatusesAsync(Guid runId) =>
        _inner.GetStepStatusesAsync(runId);

    public Task<IReadOnlyCollection<string>> GetClaimedStepKeysAsync(Guid runId) =>
        _inner.GetClaimedStepKeysAsync(runId);

    public Task<bool> TryClaimStepAsync(Guid runId, string stepKey) => Task.FromResult(true);

    public Task RecordSkippedStepAsync(Guid runId, string stepKey, string stepType, string? reason) =>
        _inner.RecordSkippedStepAsync(runId, stepKey, stepType, reason);

    public Task<string?> GetRunStatusAsync(Guid runId) => _inner.GetRunStatusAsync(runId);
}
