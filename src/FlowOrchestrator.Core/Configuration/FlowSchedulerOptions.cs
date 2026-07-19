namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// Configuration for the Hangfire recurring-job scheduler integration.
/// Applied via <c>FlowOrchestratorBuilder.WithScheduler()</c>.
/// </summary>
public sealed class FlowSchedulerOptions
{
    /// <summary>
    /// When <see langword="true"/>, schedule overrides (pause state and cron expression overrides
    /// set via the dashboard) are persisted to <c>IFlowScheduleStateStore</c> and reapplied on restart.
    /// When <see langword="false"/>, overrides use the ephemeral in-memory store and are lost on process restart.
    /// </summary>
    public bool PersistOverrides { get; set; } = true;
}

/// <summary>
/// Configuration for run-level control features: timeouts and idempotency.
/// Applied via <c>FlowOrchestratorBuilder.WithRunControl()</c>.
/// </summary>
public sealed class FlowRunControlOptions
{
    /// <summary>
    /// Default timeout applied to every run. Enforcement is twofold: lazily when a step is dispatched
    /// after the deadline (the step is skipped and the run marked <c>TimedOut</c>), and proactively by
    /// the periodic sweep governed by <see cref="TimeoutEnforcementInterval"/>. A per-run override may
    /// be supplied via the trigger payload's <c>runTimeoutSeconds</c>. <see langword="null"/> disables
    /// the default timeout globally.
    /// </summary>
    public TimeSpan? DefaultRunTimeout { get; set; }

    /// <summary>
    /// How often the periodic timeout-enforcement sweep runs, proactively marking runs whose
    /// <c>TimeoutAtUtc</c> has passed as <c>TimedOut</c> so a stuck run does not sit <c>Running</c>
    /// indefinitely. <see langword="null"/> or a non-positive value disables the sweep.
    /// </summary>
    /// <remarks>
    /// The sweep is cheap when no run has a deadline (no <see cref="DefaultRunTimeout"/> and no per-run
    /// override) — it queries active runs and finds nothing to do.
    /// </remarks>
    public TimeSpan? TimeoutEnforcementInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Name of the HTTP header that carries the caller-supplied idempotency key
    /// (e.g. <c>"Idempotency-Key"</c>). Requests carrying a key that matches an
    /// existing run are de-duplicated rather than creating a new run.
    /// </summary>
    public string IdempotencyHeaderName { get; set; } = "Idempotency-Key";
}

/// <summary>
/// Configuration for the background data retention sweep.
/// Applied via <c>FlowOrchestratorBuilder.WithRetention()</c>.
/// </summary>
public sealed class FlowRetentionOptions
{
    /// <summary>
    /// Enables the periodic retention cleanup hosted service.
    /// When <see langword="false"/>, no automatic deletion occurs regardless of other settings.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum age of completed run data before it is eligible for deletion.
    /// Measured from <see cref="FlowOrchestrator.Core.Storage.FlowRunRecord.CompletedAt"/>.
    /// </summary>
    public TimeSpan DataTtl { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the background cleanup sweep runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Configuration for OpenTelemetry instrumentation and event persistence.
/// Applied via <c>FlowOrchestratorBuilder.WithObservability()</c>.
/// </summary>
public sealed class FlowObservabilityOptions
{
    /// <summary>
    /// When <see langword="true"/>, flow and step lifecycle events are persisted to
    /// <see cref="FlowOrchestrator.Core.Storage.IOutputsRepository.RecordEventAsync"/>
    /// and retrievable via <see cref="FlowOrchestrator.Core.Storage.IFlowEventReader"/>.
    /// </summary>
    public bool EnableEventPersistence { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, emits OpenTelemetry metrics (counters, histograms) and
    /// distributed trace spans via <c>System.Diagnostics.Activity</c> and <c>Meter</c>.
    /// </summary>
    public bool EnableOpenTelemetry { get; set; } = true;
}
