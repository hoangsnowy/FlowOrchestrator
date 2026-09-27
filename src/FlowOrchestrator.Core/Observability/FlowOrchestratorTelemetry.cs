using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Singleton telemetry hub exposing the <c>FlowOrchestrator</c> <see cref="System.Diagnostics.ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/> used for distributed tracing and metrics.
/// Wire up <c>AddOpenTelemetry()</c> and <c>AddFlowOrchestratorInstrumentation()</c> to export
/// spans and counters to your observability backend.
/// </summary>
/// <remarks>
/// Moved from <c>FlowOrchestrator.Core.Execution</c> in v1.19 so OpenTelemetry registration is
/// independent of any specific runtime adapter (Hangfire, in-memory, queue). The activity source
/// and meter names are unchanged — all existing OTel pipelines continue to work without changes.
/// </remarks>
public sealed class FlowOrchestratorTelemetry : IDisposable
{
    /// <summary>Name shared by both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.</summary>
    public const string SourceName = "FlowOrchestrator";

    /// <summary>
    /// Library-wide static activity source. Used from contexts where DI is not available
    /// (e.g. <see cref="FlowOrchestrator.Core.Execution.PollableStepHandler{T}"/> base class
    /// and the InMemory runtime's channel runner). Listeners subscribed to
    /// <see cref="SourceName"/> receive activities from this source and from the per-instance
    /// <see cref="ActivitySource"/> identically — multiple
    /// <see cref="System.Diagnostics.ActivitySource"/> instances with the same name is the
    /// supported way to emit from many compilation units without round-tripping through DI.
    /// </summary>
    public static readonly ActivitySource SharedActivitySource = new(SourceName);

    /// <summary>OpenTelemetry activity source for distributed tracing of flow and step executions.</summary>
    public ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>OpenTelemetry meter for emitting counters and histograms.</summary>
    public Meter Meter { get; } = new(SourceName, "1.0.0");

    /// <summary>Incremented each time a new flow run is triggered.</summary>
    public Counter<long> RunStartedCounter { get; }

    /// <summary>Incremented each time a flow run reaches a terminal state (succeeded, failed, or cancelled).</summary>
    public Counter<long> RunCompletedCounter { get; }

    /// <summary>Incremented each time any step reaches a terminal state.</summary>
    public Counter<long> StepCompletedCounter { get; }

    /// <summary>Records the wall-clock duration of each step execution in milliseconds.</summary>
    public Histogram<double> StepDurationMs { get; }

    /// <summary>Records the delay between step enqueue time and actual execution start in milliseconds.</summary>
    public Histogram<double> QueueDelayMs { get; }

    /// <summary>Incremented every time a failed step is dispatched for retry.</summary>
    public Counter<long> StepRetriesCounter { get; }

    /// <summary>Incremented every time a step is skipped (false <c>When</c> clause or unmet <c>RunAfter</c>).</summary>
    public Counter<long> StepSkippedCounter { get; }

    /// <summary>Incremented for each polling attempt of a <c>PollableStepHandler</c>.</summary>
    public Counter<long> StepPollAttemptsCounter { get; }

    /// <summary>
    /// Records how long the runtime adapter took to accept a step dispatch — the
    /// <c>IStepDispatcher.EnqueueStepAsync</c> / <c>ScheduleStepAsync</c> call itself, in milliseconds.
    /// Tags: <c>runtime</c> (<c>hangfire</c> / <c>in_memory</c> / <c>service_bus</c> / adapter type name),
    /// <c>mode</c> (<c>enqueue</c> / <c>schedule</c>), <c>outcome</c> (<c>ok</c> / <c>error</c>).
    /// </summary>
    /// <remarks>
    /// Separates "the broker or job store was slow to accept the work" from everything else on the
    /// trigger and continuation paths, per runtime. It is the measurement #192 needs: the Service Bus
    /// runtime showed a 470x p50/p95 trigger-latency spread against the emulator, and whether that tail
    /// sits on the send cannot be told from request timings alone.
    /// </remarks>
    public Histogram<double> StepDispatchDurationMs { get; }

    /// <summary>
    /// Incremented every time <c>RunStepAsync</c> loses the execution claim and exits without running
    /// the step. Tags: <c>flow_id</c>, <c>step_key</c>.
    /// </summary>
    /// <remarks>
    /// Most losses are benign and expected: at-least-once redelivery, the duplicate resume a signal
    /// can produce (the dispatcher's nudge and the engine's own post-release re-check), and every
    /// resumed <c>WaitForSignal</c>'s orphaned safety-net attempt, which fires later and finds the step
    /// already finished. Read it as a rate, per step key: a loss is only harmful when no other attempt
    /// ran the step, and before this counter such a loss left no trace at all (#190).
    /// </remarks>
    public Counter<long> StepClaimLostCounter { get; }

    /// <summary>Records the wall-clock time a <c>WaitForSignal</c> step spent parked, in milliseconds.</summary>
    public Histogram<double> SignalWaitMs { get; }

    /// <summary>Records the gap between a cron trigger's scheduled fire time and its actual dispatch time, in milliseconds.</summary>
    public Histogram<double> CronLagMs { get; }

    /// <summary>Incremented for every webhook receive (accepted or rejected). Tags: <c>flow</c>, <c>result</c>, <c>scheme</c>.</summary>
    public Counter<long> WebhookReceivedCounter { get; }

    /// <summary>Incremented for every webhook rejected by the security pipeline. Tags: <c>flow</c>, <c>reason</c>.</summary>
    public Counter<long> WebhookRejectedCounter { get; }

    /// <summary>Records the size of each webhook body in bytes. Tags: <c>flow</c>.</summary>
    public Histogram<long> WebhookBodyBytes { get; }

    /// <summary>Records the wall-clock pipeline processing time per webhook in milliseconds. Tags: <c>flow</c>, <c>result</c>.</summary>
    public Histogram<double> WebhookProcessingMs { get; }

    /// <summary>Initialises all counters and histograms against the shared <see cref="Meter"/>.</summary>
    public FlowOrchestratorTelemetry()
    {
        RunStartedCounter = Meter.CreateCounter<long>("flow_runs_started");
        RunCompletedCounter = Meter.CreateCounter<long>("flow_runs_completed");
        StepCompletedCounter = Meter.CreateCounter<long>("flow_steps_completed");
        StepDurationMs = Meter.CreateHistogram<double>("flow_step_duration_ms");
        QueueDelayMs = Meter.CreateHistogram<double>("flow_step_queue_delay_ms");
        StepRetriesCounter = Meter.CreateCounter<long>("flow_step_retries");
        StepSkippedCounter = Meter.CreateCounter<long>("flow_step_skipped");
        StepPollAttemptsCounter = Meter.CreateCounter<long>("flow_step_poll_attempts");
        StepDispatchDurationMs = Meter.CreateHistogram<double>("flow_step_dispatch_duration_ms");
        StepClaimLostCounter = Meter.CreateCounter<long>("flow_step_claim_lost");
        SignalWaitMs = Meter.CreateHistogram<double>("flow_signal_wait_ms");
        CronLagMs = Meter.CreateHistogram<double>("flow_cron_lag_ms");
        WebhookReceivedCounter = Meter.CreateCounter<long>("webhook_received_total");
        WebhookRejectedCounter = Meter.CreateCounter<long>("webhook_rejected_total");
        WebhookBodyBytes = Meter.CreateHistogram<long>("webhook_body_bytes");
        WebhookProcessingMs = Meter.CreateHistogram<double>("webhook_processing_ms");
    }

    /// <summary>Records one runtime dispatch on <see cref="StepDispatchDurationMs"/>.</summary>
    /// <param name="runtime">Runtime tag: <c>hangfire</c>, <c>in_memory</c>, <c>service_bus</c>, or a custom adapter's name.</param>
    /// <param name="scheduled"><see langword="true"/> for <c>ScheduleStepAsync</c>, <see langword="false"/> for <c>EnqueueStepAsync</c>.</param>
    /// <param name="succeeded">Whether the adapter accepted the dispatch without throwing.</param>
    /// <param name="startTimestamp">A <see cref="Stopwatch.GetTimestamp"/> value taken before the dispatch.</param>
    public void RecordDispatch(string runtime, bool scheduled, bool succeeded, long startTimestamp) =>
        StepDispatchDurationMs.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("runtime", runtime),
            new KeyValuePair<string, object?>("mode", scheduled ? "schedule" : "enqueue"),
            new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error"));

    /// <summary>Disposes the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
