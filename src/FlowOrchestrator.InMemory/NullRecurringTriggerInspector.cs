using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// No-op <see cref="IRecurringTriggerInspector"/> used by the in-memory runtime.
/// Always returns an empty list because the in-memory runtime registers no cron jobs.
/// </summary>
internal sealed class NullRecurringTriggerInspector : IRecurringTriggerInspector
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RecurringTriggerInfo>> GetJobsAsync()
        => Task.FromResult<IReadOnlyList<RecurringTriggerInfo>>(Array.Empty<RecurringTriggerInfo>());
}
