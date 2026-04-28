using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// No-op <see cref="IRecurringTriggerDispatcher"/> used by the in-memory runtime.
/// Cron-triggered flows are not supported without a scheduler backend (e.g. Hangfire).
/// All operations complete silently without effect.
/// </summary>
internal sealed class NullRecurringTriggerDispatcher : IRecurringTriggerDispatcher
{
    /// <inheritdoc/>
    /// <remarks>No-op in the in-memory runtime. Cron jobs are not scheduled.</remarks>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression) { }

    /// <inheritdoc/>
    public void Remove(string jobId) { }

    /// <inheritdoc/>
    public void TriggerOnce(string jobId) { }

    /// <inheritdoc/>
    public Task EnqueueTriggerAsync(Guid flowId, string triggerKey, CancellationToken ct = default)
        => Task.CompletedTask;
}
