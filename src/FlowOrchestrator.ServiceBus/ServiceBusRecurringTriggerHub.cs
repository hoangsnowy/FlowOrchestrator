using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Cronos;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Single-class implementation of <see cref="IRecurringTriggerDispatcher"/>,
/// <see cref="IRecurringTriggerInspector"/>, and <see cref="IRecurringTriggerSync"/>
/// for the Service Bus runtime. Schedules cron-trigger fires as
/// <see cref="ServiceBusMessage.ScheduledEnqueueTime">scheduled messages</see>
/// on the cron queue; the consumer (<see cref="ServiceBusCronProcessorHostedService"/>)
/// drains them, calls the engine, and self-perpetuates the next fire.
/// </summary>
/// <remarks>
/// Multi-replica safety: Service Bus delivers each scheduled message exactly once to a
/// single consumer, so two replicas competing on the same queue cannot fire the same
/// cron tick twice. Duplicate detection on the queue (10-min window by default) eats
/// any sync-time duplicates that arise when multiple replicas restart simultaneously
/// and both compute the same <c>{flowId}:{triggerKey}:{nextFire}</c> message id.
/// </remarks>
internal sealed class ServiceBusRecurringTriggerHub
    : IRecurringTriggerDispatcher, IRecurringTriggerInspector, IRecurringTriggerSync, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly IFlowRepository _repository;
    private readonly IFlowScheduleStateStore _scheduleStateStore;
    private readonly FlowSchedulerOptions _schedulerOptions;
    private readonly ILogger<ServiceBusRecurringTriggerHub> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly Lazy<ServiceBusSender> _sender;

    /// <summary>Initialises the hub.</summary>
    public ServiceBusRecurringTriggerHub(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        IFlowRepository repository,
        IFlowScheduleStateStore scheduleStateStore,
        FlowSchedulerOptions schedulerOptions,
        ILogger<ServiceBusRecurringTriggerHub> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _options = options;
        _repository = repository;
        _scheduleStateStore = scheduleStateStore;
        _schedulerOptions = schedulerOptions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sender = new Lazy<ServiceBusSender>(() => _client.CreateSender(_options.CronQueueName));
    }

    // ── IRecurringTriggerDispatcher ──────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression)
    {
        var now = _timeProvider.GetUtcNow();
        var nextFire = ComputeNext(cronExpression, now);

        _jobs.AddOrUpdate(jobId,
            _ => new JobState
            {
                FlowId = flowId,
                TriggerKey = triggerKey,
                EffectiveCron = cronExpression,
                NextExecution = nextFire,
            },
            (_, existing) =>
            {
                existing.FlowId = flowId;
                existing.TriggerKey = triggerKey;
                existing.EffectiveCron = cronExpression;
                existing.NextExecution = nextFire;
                existing.Paused = false;
                return existing;
            });

        // Fire-and-forget enqueue — keeping RegisterOrUpdate synchronous matches the
        // PeriodicTimer dispatcher's signature. ScheduleNextAsync now throws on failure so the
        // self-perpetuating consumer can redeliver; for the registration-time call we still log
        // and continue, since FlowSyncHostedService will re-attempt via the next sync cycle.
        _ = ScheduleNextAsync(jobId, flowId, triggerKey, cronExpression, nextFire, CancellationToken.None)
            .ContinueWith(t =>
                _logger.LogError(t.Exception, "Failed to schedule first firing for {JobId} at {FireAt:O}.", jobId, nextFire),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _logger.LogInformation("Registered recurring job {JobId} (cron='{Cron}').", jobId, cronExpression);
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        if (_jobs.TryRemove(jobId, out var state))
        {
            _logger.LogInformation("Removed recurring job {JobId}.", jobId);
            // Best-effort cancellation of any in-flight scheduled message.
            if (state.LastSequenceNumber is { } seq)
            {
                _ = TryCancelScheduledAsync(seq);
            }
        }
    }

    /// <inheritdoc/>
    public void TriggerOnce(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;

        // Schedule a one-off message for "now" — picked up immediately by the cron consumer.
        _ = EnqueueCronMessageAsync(
            jobId,
            state.FlowId,
            state.TriggerKey,
            state.EffectiveCron,
            _timeProvider.GetUtcNow(),
            CancellationToken.None);
    }

    /// <inheritdoc/>
    public Task EnqueueTriggerAsync(Guid flowId, string triggerKey, CancellationToken ct = default)
    {
        var jobId = $"flow-{flowId}-{triggerKey}";
        return EnqueueCronMessageAsync(jobId, flowId, triggerKey, cron: string.Empty, _timeProvider.GetUtcNow(), ct);
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
        }
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Records observed execution metadata after the cron consumer fires the trigger.
    /// Called by <see cref="ServiceBusCronProcessorHostedService"/>.
    /// </summary>
    internal void NoteFireResult(string jobId, string state, DateTimeOffset firedAt)
    {
        if (_jobs.TryGetValue(jobId, out var js))
        {
            js.LastExecution = firedAt;
            js.LastJobId = jobId;
            js.LastJobState = state;
        }
    }

    /// <summary>
    /// Schedules the NEXT cron firing for an already-registered job. Throws on enqueue failure
    /// so the caller (cron consumer) can abandon the current message and let Service Bus
    /// redeliver — the alternative of swallowing the error would silently kill the cron until
    /// host restart.
    /// </summary>
    /// <remarks>
    /// Short-circuits when <paramref name="jobId"/> is no longer in the hub's job map. This guards
    /// against the v1.22 disabled-flow self-perpetuation bug: the cron consumer calls this method
    /// BEFORE invoking the engine, so a flow that was disabled mid-flight (which calls
    /// <see cref="Remove(string)"/> via <see cref="SyncTriggers(Guid, bool)"/>) would otherwise
    /// keep enqueuing scheduled messages every tick even though the engine rejects each fire.
    /// </remarks>
    internal async Task ScheduleNextAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        CancellationToken ct)
    {
        // If the job has been removed (flow disabled / unregistered), do NOT schedule the next fire.
        // Without this check the cron consumer self-perpetuates indefinitely for disabled flows.
        if (!_jobs.ContainsKey(jobId))
        {
            _logger.LogDebug(
                "Skipping ScheduleNextAsync for {JobId}: job is no longer registered (flow disabled or removed).",
                jobId);
            return;
        }

        var seq = await EnqueueCronMessageAsync(jobId, flowId, triggerKey, cron, fireAt, ct).ConfigureAwait(false);
        if (seq is { } s && _jobs.TryGetValue(jobId, out var js))
        {
            js.NextExecution = fireAt;
            js.LastSequenceNumber = s;
        }
    }

    private async Task<long?> EnqueueCronMessageAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        CancellationToken ct)
    {
        var envelope = new CronEnvelope
        {
            FlowId = flowId,
            TriggerKey = triggerKey,
            Cron = cron,
            ScheduledFor = fireAt,
            JobId = jobId,
        };
        var msg = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(envelope))
        {
            // Deterministic id: makes restart-time re-sync idempotent thanks to topic dedup.
            MessageId = $"{flowId}:{triggerKey}:{fireAt.UtcDateTime:O}",
            ContentType = "application/json",
            Subject = jobId,
        };
        msg.ApplicationProperties["FlowId"] = flowId.ToString();
        msg.ApplicationProperties["TriggerKey"] = triggerKey;
        msg.ApplicationProperties["JobId"] = jobId;

        if (fireAt > _timeProvider.GetUtcNow())
        {
            msg.ScheduledEnqueueTime = fireAt;
        }

        try
        {
            // Use ScheduleMessageAsync to get back a sequence number for later cancellation.
            var seq = await _sender.Value.ScheduleMessageAsync(msg, fireAt, ct).ConfigureAwait(false);
            return seq;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger.LogError(ex,
                "Cron queue '{Queue}' not found. Ensure topology is provisioned.",
                _options.CronQueueName);
            throw;
        }
    }

    private async Task TryCancelScheduledAsync(long sequenceNumber)
    {
        try
        {
            await _sender.Value.CancelScheduledMessageAsync(sequenceNumber).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cancel scheduled message {Seq} (already delivered or queue gone).", sequenceNumber);
        }
    }

    internal static DateTimeOffset ComputeNext(string cron, DateTimeOffset baseUtc)
    {
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = fields.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        var expr = CronExpression.Parse(cron, format);
        var next = expr.GetNextOccurrence(baseUtc.UtcDateTime, TimeZoneInfo.Utc);
        return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : DateTimeOffset.MaxValue;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal bool TryGetJob(string jobId, out (Guid FlowId, string TriggerKey, string EffectiveCron, DateTimeOffset NextExecution) snapshot)
    {
        if (_jobs.TryGetValue(jobId, out var s))
        {
            snapshot = (s.FlowId, s.TriggerKey, s.EffectiveCron, s.NextExecution);
            return true;
        }
        snapshot = default;
        return false;
    }

    private sealed class JobState
    {
        public Guid FlowId;
        public string TriggerKey = "";
        public string EffectiveCron = "";
        public DateTimeOffset NextExecution;
        public DateTimeOffset? LastExecution;
        public string? LastJobId;
        public string? LastJobState;
        public bool Paused;
        public long? LastSequenceNumber;
    }
}
