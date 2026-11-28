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
/// Timer-based <see cref="IRecurringTriggerDispatcher"/> and <see cref="IRecurringTriggerInspector"/>
/// for the in-memory runtime.
/// Provides cron scheduling parity with Hangfire without an external job store.
/// </summary>
/// <remarks>
/// A single <see cref="PeriodicTimer"/> ticks every second; on each tick, due jobs are fired by
/// invoking <see cref="IFlowOrchestrator.TriggerByScheduleAsync"/> within a fresh DI scope.
/// Job state lives in process memory and is rebuilt on each start-up by <c>FlowSyncHostedService</c>
/// through Core's <c>RecurringTriggerSync</c>, which resolves the effective cron (pause state,
/// persisted override) and calls <see cref="RegisterOrUpdate"/> / <see cref="Remove"/>.
/// </remarks>
internal sealed partial class PeriodicTimerRecurringTriggerDispatcher
    : IRecurringTriggerDispatcher, IRecurringTriggerInspector, IHostedService, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
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
        ILogger<PeriodicTimerRecurringTriggerDispatcher> logger,
        TimeProvider? timeProvider = null,
        FlowOrchestratorTelemetry? telemetry = null)
    {
        _services = services;
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
        // Dispose path: any exception (incl. OCE) must not break shutdown.
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not null) { /* swallow on shutdown */ }
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

                    // Per-job isolation: the loop is shared by every cron job in the process, so nothing one
                    // job does may end it. Only the loop's own cancellation (shutdown) propagates.
                    try
                    {
                        await FireAsync(jobId, state, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        LogJobFireFailed(_logger, ex, Sanitize(jobId), state.FlowId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogLoopTerminated(_logger, ex);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            state.LastJobId = jobId;
            state.LastJobState = "Failed";
            LogJobFailed(_logger, ex, Sanitize(jobId), state.FlowId);
            // Swallow so a single failure cannot kill the timer loop.
        }
        finally
        {
            // The job's cron was parsed when it was registered (RegisterOrUpdate rejects an unparseable one
            // without touching the job), so this cannot throw today. It is still guarded: a throw from a
            // finally block escapes FireAsync, and before per-job isolation it ended the timer loop for every
            // job in the process. A job whose next occurrence cannot be computed stops firing instead.
            try
            {
                state.NextExecution = ComputeNext(state.ParsedCron, _timeProvider.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.NextExecution = DateTimeOffset.MaxValue;
                LogNextOccurrenceFailed(_logger, ex, Sanitize(jobId));
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="cron"/> with the same 5-field / 6-field detection every runtime uses.
    /// </summary>
    /// <param name="cron">A 5-field (minute precision) or 6-field (second precision) cron expression.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cron"/> is blank or not a valid cron expression; the inner exception
    /// carries the parser's reason.
    /// </exception>
    internal static CronExpression ParseCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            throw new ArgumentException("A cron expression is required.", nameof(cron));
        }

        // Cronos accepts both 5-field (minute-precision) and 6-field (second-precision) expressions.
        var trimmed = cron.Trim();
        var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = fields.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try
        {
            return CronExpression.Parse(trimmed, format);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"'{Sanitize(cron)}' is not a valid cron expression: {ex.Message}", nameof(cron), ex);
        }
    }

    private static DateTimeOffset ComputeNext(CronExpression expr, DateTimeOffset baseUtc)
    {
        var next = expr.GetNextOccurrence(baseUtc.UtcDateTime, TimeZoneInfo.Utc);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : DateTimeOffset.MaxValue;
    }

    // Job ids and cron expressions can arrive from dashboard URLs and bodies; strip line breaks so they
    // cannot forge log entries. Cold path (registration, failures), so the chained Replace is fine.
    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');

    // ── IRecurringTriggerDispatcher ──────────────────────────────────────────

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cronExpression"/> cannot be parsed. The job is left exactly as it was — an
    /// existing job keeps firing on its previous schedule — the same contract as Hangfire's
    /// <c>RecurringJobManager.AddOrUpdate</c>.
    /// </exception>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression)
    {
        // Parse BEFORE touching the job. The update path used to assign the new cron first and parse it
        // second, so a rejected override still replaced the live job's cron; the next fire then threw from
        // FireAsync's finally block and ended the timer loop shared by every cron job in the process.
        var parsed = ParseCron(cronExpression);
        var nextExecution = ComputeNext(parsed, _timeProvider.GetUtcNow());

        _jobs.AddOrUpdate(jobId,
            _ => new JobState
            {
                FlowId = flowId,
                TriggerKey = triggerKey,
                EffectiveCron = cronExpression,
                ParsedCron = parsed,
                NextExecution = nextExecution,
            },
            (_, existing) =>
            {
                // The caller passes the already-effective cron (RecurringTriggerSync resolves the
                // persisted override; the dashboard passes the new override), so it always wins —
                // the same replace semantics as Hangfire's AddOrUpdate.
                existing.FlowId = flowId;
                existing.TriggerKey = triggerKey;
                existing.EffectiveCron = cronExpression;
                existing.ParsedCron = parsed;
                existing.NextExecution = nextExecution;
                existing.Paused = false;
                return existing;
            });

        LogRegistered(_logger, Sanitize(jobId), Sanitize(cronExpression));
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        if (_jobs.TryRemove(jobId, out _))
        {
            LogRemoved(_logger, Sanitize(jobId));
        }
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEnqueueTriggerFailed(_logger, ex, flowId, Sanitize(triggerKey));
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

    [LoggerMessage(EventId = 3120, Level = LogLevel.Information, Message = "Registered recurring job {JobId} with cron '{Cron}'.")]
    private static partial void LogRegistered(ILogger logger, string jobId, string cron);

    [LoggerMessage(EventId = 3121, Level = LogLevel.Information, Message = "Removed recurring job {JobId}.")]
    private static partial void LogRemoved(ILogger logger, string jobId);

    [LoggerMessage(EventId = 3122, Level = LogLevel.Error, Message = "Recurring job {JobId} (Flow={FlowId}) failed.")]
    private static partial void LogJobFailed(ILogger logger, Exception ex, string jobId, Guid flowId);

    [LoggerMessage(EventId = 3123, Level = LogLevel.Error, Message = "Firing recurring job {JobId} (Flow={FlowId}) threw; the timer loop continues with the other jobs.")]
    private static partial void LogJobFireFailed(ILogger logger, Exception ex, string jobId, Guid flowId);

    [LoggerMessage(EventId = 3124, Level = LogLevel.Error, Message = "Recurring job {JobId}: the next occurrence could not be computed; the job stops firing until it is re-registered.")]
    private static partial void LogNextOccurrenceFailed(ILogger logger, Exception ex, string jobId);

    [LoggerMessage(EventId = 3125, Level = LogLevel.Critical, Message = "Recurring-trigger loop terminated unexpectedly; no cron job fires in this process until restart.")]
    private static partial void LogLoopTerminated(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3126, Level = LogLevel.Error, Message = "EnqueueTrigger for Flow={FlowId} TriggerKey={TriggerKey} failed.")]
    private static partial void LogEnqueueTriggerFailed(ILogger logger, Exception ex, Guid flowId, string triggerKey);

    private sealed class JobState
    {
        public Guid FlowId;
        public string TriggerKey = "";
        public string EffectiveCron = "";
        public CronExpression ParsedCron = null!;
        public DateTimeOffset NextExecution;
        public DateTimeOffset? LastExecution;
        public string? LastJobId;
        public string? LastJobState;
        public bool Paused;
    }
}
