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
        var safeJobId = jobId.Replace('\r', '_').Replace('\n', '_');
        var safeCron = cronExpression.Replace('\r', '_').Replace('\n', '_');
        _ = ScheduleNextAsync(jobId, flowId, triggerKey, cronExpression, nextFire, CancellationToken.None)
            .ContinueWith(t =>
                _logger.LogError(t.Exception, "Failed to schedule first firing for {JobId} at {FireAt:O}.", safeJobId, nextFire),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        _logger.LogInformation("Registered recurring job {JobId} (cron='{Cron}').", safeJobId, safeCron);
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        if (_jobs.TryRemove(jobId, out var state))
        {
            _logger.LogInformation("Removed recurring job {JobId}.", jobId.Replace('\r', '_').Replace('\n', '_'));
            // Best-effort cancellation of any in-flight scheduled message.
            if (state.LastSequenceNumber is { } seq)
            {
                _ = TryCancelScheduledAsync(seq);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Enqueues the one-off message with an EMPTY cron so the cron consumer
    /// (<see cref="ServiceBusCronProcessorHostedService.OnMessageAsync"/>) does NOT self-perpetuate
    /// a follow-up firing — its perpetuation branch only runs when <c>envelope.Cron</c> is non-empty.
    /// Passing <see cref="JobState.EffectiveCron"/> here would fork the schedule: the manual "Trigger now"
    /// message would spawn its own recurring chain on top of the existing one, firing the job twice
    /// forever. The already-scheduled chain keeps the recurrence alive, so the manual fire is purely additive.
    /// </remarks>
    public void TriggerOnce(string jobId)
    {
        // Schedule a one-off message for "now" — picked up immediately by the cron consumer.
        // cron: string.Empty => consumer fires once and does not perpetuate (see remarks).
        if (!TryBuildTriggerOnceMessage(jobId, out var flowId, out var triggerKey, out _))
        {
            return;
        }

        _ = EnqueueCronMessageAsync(
            jobId,
            flowId,
            triggerKey,
            cron: string.Empty,
            _timeProvider.GetUtcNow(),
            CancellationToken.None);
    }

    /// <summary>
    /// Resolves the registered job's identity for a one-off <see cref="TriggerOnce"/> fire and
    /// exposes the one-off <see cref="ServiceBusMessage"/> it would enqueue. Exists as a testable
    /// seam: the unit suite asserts that the produced message carries an EMPTY
    /// <see cref="CronEnvelope.Cron"/> so the cron consumer does not fork the recurring schedule.
    /// </summary>
    /// <param name="jobId">The registered job to fire once.</param>
    /// <param name="flowId">When found, the resolved flow id.</param>
    /// <param name="triggerKey">When found, the resolved trigger key.</param>
    /// <param name="message">When found, the one-off message (empty cron) that would be enqueued.</param>
    /// <returns><see langword="true"/> when the job is registered; otherwise <see langword="false"/>.</returns>
    internal bool TryBuildTriggerOnceMessage(string jobId, out Guid flowId, out string triggerKey, out ServiceBusMessage? message)
    {
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            flowId = default;
            triggerKey = string.Empty;
            message = null;
            return false;
        }

        flowId = state.FlowId;
        triggerKey = state.TriggerKey;
        var now = _timeProvider.GetUtcNow();
        message = BuildCronMessage(jobId, flowId, triggerKey, cron: string.Empty, now, now);
        return true;
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
                jobId.Replace('\r', '_').Replace('\n', '_'));
            return;
        }

        var seq = await EnqueueCronMessageAsync(jobId, flowId, triggerKey, cron, fireAt, ct).ConfigureAwait(false);
        if (seq is { } s && _jobs.TryGetValue(jobId, out var js))
        {
            js.NextExecution = fireAt;
            js.LastSequenceNumber = s;
        }
    }

    /// <summary>
    /// Builds the <see cref="ServiceBusMessage"/> (and embedded <see cref="CronEnvelope"/>) for a
    /// cron firing without touching the sender. Extracted as an <c>internal static</c> seam so the
    /// unit suite can assert envelope semantics — most importantly that <see cref="TriggerOnce"/>
    /// emits an EMPTY <see cref="CronEnvelope.Cron"/> (one-off, no self-perpetuation) — without a
    /// live Service Bus connection.
    /// </summary>
    /// <param name="jobId">Stable scheduler job identifier.</param>
    /// <param name="flowId">Flow whose cron trigger should fire.</param>
    /// <param name="triggerKey">Manifest trigger key.</param>
    /// <param name="cron">
    /// Cron expression to embed. Pass <see cref="string.Empty"/> for a one-off fire so the consumer
    /// does not schedule a follow-up tick.
    /// </param>
    /// <param name="fireAt">UTC instant the message should fire.</param>
    /// <param name="now">Current UTC instant, used to decide whether to set <c>ScheduledEnqueueTime</c>.</param>
    /// <returns>A fully-populated message ready to hand to <c>ScheduleMessageAsync</c>.</returns>
    internal static ServiceBusMessage BuildCronMessage(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        DateTimeOffset now)
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

        if (fireAt > now)
        {
            msg.ScheduledEnqueueTime = fireAt;
        }

        return msg;
    }

    private async Task<long?> EnqueueCronMessageAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        CancellationToken ct)
    {
        var msg = BuildCronMessage(jobId, flowId, triggerKey, cron, fireAt, _timeProvider.GetUtcNow());

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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
