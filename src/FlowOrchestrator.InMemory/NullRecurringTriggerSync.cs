using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// No-op <see cref="IRecurringTriggerSync"/> used by the in-memory runtime.
/// Enable/disable calls on flows with cron triggers are silently ignored
/// because the in-memory runtime does not maintain a scheduler.
/// </summary>
internal sealed class NullRecurringTriggerSync : IRecurringTriggerSync
{
    /// <inheritdoc/>
    public void SyncTriggers(Guid flowId, bool isEnabled) { }
}
