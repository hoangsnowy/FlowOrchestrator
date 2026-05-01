using System.Diagnostics;
using FlowOrchestrator.Core.Observability;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;

namespace FlowOrchestrator.Hangfire.Telemetry;

/// <summary>
/// Hangfire client + server filter that propagates the W3C <c>traceparent</c> header across the
/// enqueue / dequeue boundary, restoring distributed-trace continuity that Hangfire's
/// argument serializer otherwise drops.
/// </summary>
/// <remarks>
/// On enqueue (<see cref="IClientFilter.OnCreating"/>) we capture <see cref="Activity.Current"/>'s
/// W3C identifiers and store them on Hangfire job parameters. On execute
/// (<see cref="IServerFilter.OnPerforming"/>) we read those parameters back and open a wrapper
/// activity (<c>flow.runtime.execute</c>) whose parent context is the captured one. Subsequent
/// activities opened by the engine — <c>flow.trigger</c>, <c>flow.step</c> — automatically become
/// children of the wrapper, so APMs see a single connected trace from the original caller through
/// to every step's execution.
/// </remarks>
internal sealed class TraceContextHangfireFilter : JobFilterAttribute, IClientFilter, IServerFilter
{
    private const string TraceparentParam = "flow_traceparent";
    private const string TracestateParam = "flow_tracestate";
    private const string ItemsKey = "flow_runtime_activity";

    private readonly ActivitySource _activitySource;

    /// <summary>Creates a filter that propagates trace context using the shared FlowOrchestrator activity source.</summary>
    public TraceContextHangfireFilter()
        : this(new ActivitySource(FlowOrchestratorTelemetry.SourceName))
    {
    }

    /// <summary>Creates a filter that propagates trace context using the supplied activity source. Used by tests.</summary>
    public TraceContextHangfireFilter(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
    }

    /// <inheritdoc />
    public void OnCreating(CreatingContext filterContext)
    {
        var current = Activity.Current;
        if (current is null || current.IdFormat != ActivityIdFormat.W3C)
        {
            return;
        }

        // Capture the W3C identifiers — these survive Hangfire's job-argument serialisation as
        // simple strings, unlike ActivityContext which Hangfire would not know how to round-trip.
        filterContext.SetJobParameter(TraceparentParam, current.Id);
        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            filterContext.SetJobParameter(TracestateParam, current.TraceStateString);
        }
    }

    /// <inheritdoc />
    public void OnCreated(CreatedContext filterContext)
    {
        // No-op — the parameter was already set in OnCreating.
    }

    /// <inheritdoc />
    public void OnPerforming(PerformingContext filterContext)
    {
        var traceparent = filterContext.GetJobParameter<string?>(TraceparentParam);
        if (string.IsNullOrEmpty(traceparent))
        {
            return;
        }

        var tracestate = filterContext.GetJobParameter<string?>(TracestateParam);
        if (!ActivityContext.TryParse(traceparent, tracestate, out var parentContext))
        {
            return;
        }

        // Open a wrapper that becomes the parent of every span the engine opens for this job.
        // ActivityKind.Consumer matches the OTel messaging convention — this code consumed a
        // job from the queue and is now executing it.
        var activity = _activitySource.StartActivity(
            "flow.runtime.execute",
            ActivityKind.Consumer,
            parentContext);

        if (activity is not null)
        {
            activity.SetTag("messaging.system", "hangfire");
            activity.SetTag("messaging.operation", "process");
            activity.SetTag("messaging.message.id", filterContext.BackgroundJob.Id);
            filterContext.Items[ItemsKey] = activity;
        }
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext filterContext)
    {
        if (!filterContext.Items.TryGetValue(ItemsKey, out var raw) || raw is not Activity activity)
        {
            return;
        }

        if (filterContext.Exception is not null)
        {
            activity.RecordError(filterContext.Exception);
        }

        activity.Dispose();
        filterContext.Items.Remove(ItemsKey);
    }
}
