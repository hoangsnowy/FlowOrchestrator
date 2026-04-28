using FlowOrchestrator.Core.Execution;
using Hangfire;
using Hangfire.Storage;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Hangfire implementation of <see cref="IRecurringTriggerInspector"/>.
/// Queries the Hangfire job storage via <c>JobStorage.Current</c> to list registered recurring jobs.
/// </summary>
internal sealed class HangfireRecurringTriggerInspector : IRecurringTriggerInspector
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RecurringTriggerInfo>> GetJobsAsync()
    {
        using var connection = JobStorage.Current.GetConnection();
        var jobs = connection.GetRecurringJobs();

        IReadOnlyList<RecurringTriggerInfo> result = jobs
            .Select(j => new RecurringTriggerInfo(
                Id: j.Id,
                Cron: j.Cron,
                NextExecution: j.NextExecution,
                LastExecution: j.LastExecution,
                LastJobId: j.LastJobId,
                LastJobState: j.LastJobState,
                TimeZoneId: j.TimeZoneId))
            .ToList();

        return Task.FromResult(result);
    }
}
