using System.Diagnostics;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Helpers for the System.Diagnostics activity API used by the engine.
/// </summary>
/// <remarks>
/// Implemented manually rather than depending on the <c>OpenTelemetry.Api</c>
/// <c>Activity.RecordException</c> extension so <c>FlowOrchestrator.Core</c> stays free of any
/// OpenTelemetry runtime dependency. The event shape (event name <c>"exception"</c> with
/// <c>exception.type</c>, <c>exception.message</c>, <c>exception.stacktrace</c> tags) matches the
/// OpenTelemetry semantic convention so APMs treat it identically.
/// </remarks>
public static class ActivityExtensions
{
    /// <summary>
    /// Records <paramref name="ex"/> on the activity following the OpenTelemetry exception
    /// semantic convention and marks the activity status as <see cref="ActivityStatusCode.Error"/>.
    /// Safe to call with a <see langword="null"/> activity.
    /// </summary>
    /// <param name="activity">The current activity, or <see langword="null"/> when tracing is disabled.</param>
    /// <param name="ex">The exception to record.</param>
    public static void RecordError(this Activity? activity, Exception ex)
    {
        if (activity is null || ex is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent(
            "exception",
            timestamp: default,
            tags: new ActivityTagsCollection
            {
                ["exception.type"] = ex.GetType().FullName,
                ["exception.message"] = ex.Message,
                ["exception.stacktrace"] = ex.ToString(),
            }));
    }
}
