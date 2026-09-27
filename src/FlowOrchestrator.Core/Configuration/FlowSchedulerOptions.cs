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

/// <summary>
/// Configuration for the built-in <c>WaitForSignal</c> step type.
/// Applied via <c>FlowOrchestratorBuilder.Signals</c>.
/// </summary>
public sealed class FlowSignalOptions
{
    /// <summary>The default for <see cref="IndefiniteParkInterval"/>: 24 hours.</summary>
    public static readonly TimeSpan DefaultIndefiniteParkInterval = TimeSpan.FromHours(24);

    /// <summary>The largest honoured <see cref="IndefiniteParkInterval"/>: 30 days.</summary>
    /// <remarks>
    /// The InMemory runtime waits out a schedule with <see cref="Task.Delay(TimeSpan)"/>, which rejects
    /// anything above ~49.7 days — the safety net would then silently never fire — and a value near
    /// <see cref="TimeSpan.MaxValue"/> overflows the <c>UtcNow + delay</c> every dispatcher computes,
    /// turning each attempt into a poison job. Larger values are clamped rather than rejected so a
    /// generous setting degrades to "a month" instead of breaking the step.
    /// </remarks>
    public static readonly TimeSpan MaxIndefiniteParkInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a <c>WaitForSignal</c> step that declares no <c>timeoutSeconds</c> parks between
    /// safety-net re-invocations. Each re-invocation re-reads the waiter and completes the step when a
    /// payload has landed, so this is the worst-case resume latency if every other resume path fails.
    /// </summary>
    /// <remarks>
    /// Each re-invocation runs the full step path — a claim, an attempt row, a <c>step.started</c> /
    /// <c>step.pending</c> event pair and a few storage round-trips — so a short interval multiplies the
    /// run history of every long-parked step. The engine wakes a parked step directly on delivery, and
    /// re-checks the waiter itself after releasing the claim (#190), so the safety net is a last resort
    /// and the 24-hour default is deliberate. Values at or below <see cref="TimeSpan.Zero"/> are
    /// ignored and the default is used; values above <see cref="MaxIndefiniteParkInterval"/> are clamped.
    /// </remarks>
    public TimeSpan IndefiniteParkInterval { get; set; } = DefaultIndefiniteParkInterval;
}
