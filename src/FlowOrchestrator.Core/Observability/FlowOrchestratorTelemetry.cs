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

    /// <summary>Records the wall-clock time a <c>WaitForSignal</c> step spent parked, in milliseconds.</summary>
    public Histogram<double> SignalWaitMs { get; }

    /// <summary>Records the gap between a cron trigger's scheduled fire time and its actual dispatch time, in milliseconds.</summary>
    public Histogram<double> CronLagMs { get; }

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
        SignalWaitMs = Meter.CreateHistogram<double>("flow_signal_wait_ms");
        CronLagMs = Meter.CreateHistogram<double>("flow_cron_lag_ms");
    }

    /// <summary>Disposes the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}
