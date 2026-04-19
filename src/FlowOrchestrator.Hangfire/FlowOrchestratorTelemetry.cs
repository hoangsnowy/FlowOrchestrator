using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Singleton telemetry hub exposing the <c>FlowOrchestrator</c> <see cref="System.Diagnostics.ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/> used for distributed tracing and metrics.
/// Wire up <c>AddOpenTelemetry()</c> and <c>AddFlowOrchestratorInstrumentation()</c> to export
/// spans and counters to your observability backend.
/// </summary>
public sealed class FlowOrchestratorTelemetry : IDisposable
{
    /// <summary>Name shared by both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.</summary>
    public const string SourceName = "FlowOrchestrator";

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

    /// <summary>Initialises all counters and histograms against the shared <see cref="Meter"/>.</summary>
    public FlowOrchestratorTelemetry()
    {
        RunStartedCounter = Meter.CreateCounter<long>("flow_runs_started");
        RunCompletedCounter = Meter.CreateCounter<long>("flow_runs_completed");
        StepCompletedCounter = Meter.CreateCounter<long>("flow_steps_completed");
        StepDurationMs = Meter.CreateHistogram<double>("flow_step_duration_ms");
        QueueDelayMs = Meter.CreateHistogram<double>("flow_step_queue_delay_ms");
    }

    /// <summary>Disposes the <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }
}

