using FlowOrchestrator.Core.Execution;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Entry point for starting flow runs via Hangfire background jobs.
/// Implemented by <see cref="HangfireFlowOrchestrator"/>.
/// </summary>
public interface IHangfireFlowTrigger
{
    /// <summary>
    /// Starts a new flow run from the given trigger context, enqueuing the entry steps as Hangfire jobs.
    /// Handles idempotency key deduplication when a key is present in the trigger headers.
    /// </summary>
    /// <param name="triggerContext">The trigger context including the flow, trigger event, and run metadata.</param>
    /// <param name="performContext">Hangfire job context providing the background job ID; <see langword="null"/> when called outside a Hangfire job.</param>
    /// <returns>An anonymous object with <c>runId</c> (Guid) and <c>duplicate</c> (bool).</returns>
    ValueTask<object?> TriggerAsync(ITriggerContext triggerContext, PerformContext? performContext = null);

    /// <summary>
    /// Resolves the flow by <paramref name="flowId"/> and starts a run via its cron trigger.
    /// Called by the Hangfire recurring job registered for the flow's schedule.
    /// </summary>
    /// <param name="flowId">The flow to trigger.</param>
    /// <param name="triggerKey">The manifest trigger key (e.g. <c>"schedule"</c>).</param>
    /// <param name="performContext">Hangfire job context; <see langword="null"/> when called outside a Hangfire job.</param>
    ValueTask<object?> TriggerByScheduleAsync(Guid flowId, string triggerKey, PerformContext? performContext = null);
}
