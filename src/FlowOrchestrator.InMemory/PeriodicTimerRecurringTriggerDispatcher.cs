using System.Collections.Concurrent;
using Cronos;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.InMemory;

/// <summary>
/// Timer-based <see cref="IRecurringTriggerDispatcher"/>, <see cref="IRecurringTriggerInspector"/>,
/// and <see cref="IRecurringTriggerSync"/> for the in-memory runtime.
/// Provides cron scheduling parity with Hangfire without an external job store.
/// </summary>
/// <remarks>
/// A single <see cref="PeriodicTimer"/> ticks every second; on each tick, due jobs are fired by
/// invoking <see cref="IFlowOrchestrator.TriggerByScheduleAsync"/> within a fresh DI scope.
/// Job state lives in process memory and is rebuilt on each start-up by <c>FlowSyncHostedService</c>.
/// </remarks>
internal sealed class PeriodicTimerRecurringTriggerDispatcher
    : IRecurringTriggerDispatcher, IRecurringTriggerInspector, IRecurringTriggerSync, IHostedService, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly IFlowRepository _repository;
    private readonly IFlowScheduleStateStore _scheduleStateStore;
    private readonly FlowSchedulerOptions _schedulerOptions;
    private readonly ILogger<PeriodicTimerRecurringTriggerDispatcher> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FlowOrchestratorTelemetry? _telemetry;
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    private PeriodicTimer? _timer;
    private Task? _loop;
    private CancellationTokenSource? _cts;

    /// <summary>Initialises the dispatcher with required services. <paramref name="telemetry"/> is optional — when omitted, cron-lag metrics are not emitted.</summary>
    public PeriodicTimerRecurringTriggerDispatcher(
        IServiceProvider services,
        IFlowRepository repository,
        IFlowScheduleStateStore scheduleStateStore,
        FlowSchedulerOptions schedulerOptions,
        ILogger<PeriodicTimerRecurringTriggerDispatcher> logger,
        TimeProvider? timeProvider = null,
        FlowOrchestratorTelemetry? telemetry = null)
    {
        _services = services;
        _repository = repository;
        _scheduleStateStore = scheduleStateStore;
        _schedulerOptions = schedulerOptions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TickInterval);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel first, then drain the loop, then dispose the timer.
        // Disposing the timer before the loop drains can race WaitForNextTickAsync into
        // an unobserved ObjectDisposedException, separate from the expected OCE.
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
        _timer?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Synchronous fallback for DI containers that only call IDisposable.Dispose.
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { /* swallow on shutdown */ }
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = _timeProvider.GetUtcNow();
                foreach (var (jobId, state) in _jobs)
                {
                    if (state.Paused) continue;
                    if (state.NextExecution > now) continue;

                    await FireAsync(jobId, state, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recurring-trigger loop terminated unexpectedly.");
        }
    }

    private async Task FireAsync(string jobId, JobState state, CancellationToken ct)
    {
        // Capture cron lag: how late we are versus the scheduled fire time. NextExecution is the
        // schedule we're firing for; the loop guarantees it is <= now when FireAsync is called.
        var scheduledAt = state.NextExecution;
        var firedAt = _timeProvider.GetUtcNow();
        if (_telemetry is not null)
        {
            var lagMs = Math.Max(0, (firedAt - scheduledAt).TotalMilliseconds);
            _telemetry.CronLagMs.Record(
                lagMs,
                new KeyValuePair<string, object?>("flow_id", state.FlowId.ToString()),
                new KeyValuePair<string, object?>("trigger_key", state.TriggerKey),
                new KeyValuePair<string, object?>("runtime", "in_memory"));
        }

        try
        {
            using var scope = _services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
            await orchestrator.TriggerByScheduleAsync(state.FlowId, state.TriggerKey, jobId, ct).ConfigureAwait(false);

            state.LastExecution = _timeProvider.GetUtcNow();
            state.LastJobId = jobId;
            state.LastJobState = "Succeeded";
        }
        catch (Exception ex)
        {
            state.LastJobId = jobId;
            state.LastJobState = "Failed";
            _logger.LogError(ex, "Recurring job {JobId} (Flow={FlowId}) failed.", jobId, state.FlowId);
            // Swallow so a single failure cannot kill the timer loop.
        }
        finally
        {
            state.NextExecution = ComputeNext(state.EffectiveCron, _timeProvider.GetUtcNow());
        }
    }

    private static DateTimeOffset ComputeNext(string cron, DateTimeOffset baseUtc)
    {
        // Cronos accepts both 5-field (minute-precision) and 6-field (second-precision) expressions.
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = fields.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        var expr = CronExpression.Parse(cron, format);
        var next = expr.GetNextOccurrence(baseUtc.UtcDateTime, TimeZoneInfo.Utc);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : DateTimeOffset.MaxValue;
    }

    // ── IRecurringTriggerDispatcher ──────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression)
    {
        var now = _timeProvider.GetUtcNow();

        _jobs.AddOrUpdate(jobId,
            _ =>
            {
                var fresh = new JobState
                {
                    FlowId = flowId,
                    TriggerKey = triggerKey,
                    CronExpression = cronExpression,
                    EffectiveCron = cronExpression,
                };
                fresh.NextExecution = ComputeNext(fresh.EffectiveCron, now);
                return fresh;
            },
            (_, existing) =>
            {
                existing.FlowId = flowId;
                existing.TriggerKey = triggerKey;
                existing.CronExpression = cronExpression;
                if (string.IsNullOrWhiteSpace(existing.CronOverride))
                    existing.EffectiveCron = cronExpression;
                existing.NextExecution = ComputeNext(existing.EffectiveCron, now);
                existing.Paused = false;
                return existing;
            });

        _logger.LogInformation("Registered recurring job {JobId} with cron '{Cron}'.", jobId, cronExpression);
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        if (_jobs.TryRemove(jobId, out _))
            _logger.LogInformation("Removed recurring job {JobId}.", jobId);
    }

    /// <inheritdoc/>
    public void TriggerOnce(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        // Fire-and-forget on a thread-pool task. Schedule is preserved.
        _ = Task.Run(() => FireAsync(jobId, state, _cts?.Token ?? CancellationToken.None));
    }

    /// <inheritdoc/>
    public Task EnqueueTriggerAsync(Guid flowId, string triggerKey, CancellationToken ct = default)
    {
        // Fire-and-forget immediate one-off — bypasses cron schedule entirely.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IFlowOrchestrator>();
                await orchestrator.TriggerByScheduleAsync(flowId, triggerKey, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnqueueTrigger for Flow={FlowId} TriggerKey={TriggerKey} failed.",
                    flowId, triggerKey);
            }
        }, ct);
        return Task.CompletedTask;
    }

    // ── IRecurringTriggerInspector ───────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<RecurringTriggerInfo>> GetJobsAsync()
    {
        IReadOnlyList<RecurringTriggerInfo> result = _jobs
            .Select(kvp => new RecurringTriggerInfo(
                Id: kvp.Key,
                Cron: kvp.Value.EffectiveCron,
                NextExecution: kvp.Value.Paused ? null : kvp.Value.NextExecution.UtcDateTime,
                LastExecution: kvp.Value.LastExecution?.UtcDateTime,
                LastJobId: kvp.Value.LastJobId,
                LastJobState: kvp.Value.LastJobState,
                TimeZoneId: null))
            .ToList();
        return Task.FromResult(result);
    }

    // ── IRecurringTriggerSync ────────────────────────────────────────────────

    /// <inheritdoc/>
    public void SyncTriggers(Guid flowId, bool isEnabled)
    {
        var flows = _repository.GetAllFlowsAsync().AsTask().GetAwaiter().GetResult();
        var flow = flows.FirstOrDefault(f => f.Id == flowId);
        if (flow is null) return;

        foreach (var (triggerKey, trigger) in flow.Manifest.Triggers)
        {
            if (trigger.Type != TriggerType.Cron) continue;

            var jobId = $"flow-{flow.Id}-{triggerKey}";
            var persisted = _schedulerOptions.PersistOverrides
                ? _scheduleStateStore.GetAsync(jobId).GetAwaiter().GetResult()
                : null;

            if (!isEnabled || !trigger.TryGetCronExpression(out var manifestCron))
            {
                Remove(jobId);
                continue;
            }

            if (persisted?.IsPaused == true)
            {
                Remove(jobId);
                continue;
            }

            var effectiveCron = string.IsNullOrWhiteSpace(persisted?.CronOverride)
                ? manifestCron
                : persisted!.CronOverride!;

            RegisterOrUpdate(jobId, flow.Id, triggerKey, effectiveCron);

            // Track override separately so a future RegisterOrUpdate(manifestCron) does not lose it.
            if (_jobs.TryGetValue(jobId, out var js))
            {
                js.CronOverride = persisted?.CronOverride;
                js.EffectiveCron = effectiveCron;
            }
        }
    }

    /// <summary>Internal accessor used by tests to poke job state without the dispatcher loop.</summary>
    internal bool TryGetJob(string jobId, out (Guid FlowId, string TriggerKey, string EffectiveCron, DateTimeOffset NextExecution, DateTimeOffset? LastExecution, string? LastJobState, bool Paused) snapshot)
    {
        if (_jobs.TryGetValue(jobId, out var s))
        {
            snapshot = (s.FlowId, s.TriggerKey, s.EffectiveCron, s.NextExecution, s.LastExecution, s.LastJobState, s.Paused);
            return true;
        }

        snapshot = default;
        return false;
    }

    /// <summary>Internal hook used by tests to force-fire a job immediately.</summary>
    internal Task FireForTestAsync(string jobId, CancellationToken ct = default)
    {
        return _jobs.TryGetValue(jobId, out var state)
            ? FireAsync(jobId, state, ct)
            : Task.CompletedTask;
    }

    private sealed class JobState
    {
        public Guid FlowId;
        public string TriggerKey = "";
        public string CronExpression = "";
        public string? CronOverride;
        public string EffectiveCron = "";
        public DateTimeOffset NextExecution;
        public DateTimeOffset? LastExecution;
        public string? LastJobId;
        public string? LastJobState;
        public bool Paused;
    }
}
