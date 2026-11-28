using System.Diagnostics;

namespace FlowOrchestrator.Hangfire.Telemetry;

/// <summary>
/// Hangfire's own copy of the activity helpers Core uses internally.
/// </summary>
/// <remarks>
/// Deliberately duplicated rather than shared: Core's helper is <see langword="internal"/>, and
/// packages may be mixed at different 2.x versions, so an internal of one package must never become a
/// contract another package compiles against. The event shape (event name <c>"exception"</c> with
/// <c>exception.type</c>, <c>exception.message</c>, <c>exception.stacktrace</c> tags) matches the
/// OpenTelemetry semantic convention, the same shape Core records.
/// </remarks>
internal static class ActivityExtensions
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
