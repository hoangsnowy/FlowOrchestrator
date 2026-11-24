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
/// Single-class implementation of <see cref="IRecurringTriggerDispatcher"/> and
/// <see cref="IRecurringTriggerInspector"/> for the Service Bus runtime; effective-cron resolution
/// is Core's <see cref="RecurringTriggerSync"/>, which calls <see cref="RegisterOrUpdate"/> /
/// <see cref="Remove(string)"/>. Schedules cron-trigger fires as
/// <see cref="ServiceBusMessage.ScheduledEnqueueTime">scheduled messages</see>
/// on the cron queue; the consumer (<see cref="ServiceBusCronProcessorHostedService"/>)
/// drains them, calls the engine, and self-perpetuates the next fire.
/// </summary>
/// <remarks>
/// <para>
/// Each job is ONE self-perpetuating chain of scheduled messages. A chain is identified by its epoch
/// (<see cref="CronEnvelope.ChainEpoch"/>): the instant of its first tick, raised above every epoch the hub has
/// seen for the job, carried unchanged by every tick the chain perpetuates. The hub keeps exactly one chain
/// alive per job:
/// </para>
/// <list type="bullet">
///   <item>Re-registering a job with the cron it already runs on schedules nothing — its chain is alive.</item>
///   <item>Registering a different cron cancels the pending tick this process knows about and seeds a new
///         chain with a higher epoch; the consumer drops any tick of an older chain it still receives
///         (<see cref="ObserveTick"/>), and a replica that receives a tick of a NEWER chain than it knows
///         follows that chain, so replicas converge on the latest chain without shared state.</item>
///   <item>On the first registration after a process start (the startup sync) the hub peeks the cron queue
///         once and ADOPTS a tick the previous process left for the same job and cron instead of seeding a
///         second one. Without that, every restart seeded a tick for the same instant under the same
///         message id, and the queue's duplicate detection (10-minute window) could only absorb it when the
///         surviving tick had been sent less than 10 minutes earlier — any schedule with a longer period
///         fired twice after each restart.</item>
/// </list>
/// <para>
/// Multi-replica safety: Service Bus delivers each scheduled message exactly once to a single consumer, so
/// two replicas competing on the same queue cannot fire the same cron tick twice. Replicas starting together
/// both see (and adopt) the same surviving tick; with none, they seed the same
/// <c>{flowId}:{triggerKey}:{nextFire}</c> message id at the same moment, which duplicate detection absorbs.
/// </para>
/// </remarks>
internal sealed partial class ServiceBusRecurringTriggerHub
    : IRecurringTriggerDispatcher, IRecurringTriggerInspector, IAsyncDisposable
{
    private const int PeekPageSize = 250;
    private const int MaxInventoryMessages = 10_000;
    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);

    private readonly ServiceBusClient _client;
    private readonly ServiceBusRuntimeOptions _options;
    private readonly ILogger<ServiceBusRecurringTriggerHub> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    // Highest chain epoch seen per job. Kept apart from _jobs so that Remove (a pause) does not forget it:
    // a chain seeded on resume must still outrank every tick of the chain the pause stopped.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _latestEpoch = new(StringComparer.Ordinal);
    private readonly Lazy<ServiceBusSender> _sender;

    // Startup adoption: ticks found on the cron queue by the one peek made when the first job registers, keyed
    // by job id. Consulted only for registrations made before the consumer starts processing
    // (CompleteStartupAsync) — afterwards the snapshot is stale, because the consumer completes ticks.
    private readonly Lock _startupGate = new();
    private readonly List<Task> _startupSeeds = new();
    private Task<Dictionary<string, List<PendingTick>>>? _inventory;
    private bool _startupComplete;

    /// <summary>Initialises the hub.</summary>
    public ServiceBusRecurringTriggerHub(
        ServiceBusClient client,
        ServiceBusRuntimeOptions options,
        ILogger<ServiceBusRecurringTriggerHub> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sender = new Lazy<ServiceBusSender>(() => _client.CreateSender(_options.CronQueueName));
    }

    // ── IRecurringTriggerDispatcher ──────────────────────────────────────────

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cronExpression"/> cannot be parsed; the job is left exactly as it was.
    /// </exception>
    public void RegisterOrUpdate(string jobId, Guid flowId, string triggerKey, string cronExpression)
    {
        // Parse before touching any state: a rejected expression must leave a live job on its previous chain.
        var nextFire = ParseAndComputeNext(cronExpression, _timeProvider.GetUtcNow());

        long? supersededSequence = null;
        var seed = false;
        var adoptFromInventory = false;
        DateTimeOffset epoch = default;
        lock (_startupGate)
        {
            if (_jobs.TryGetValue(jobId, out var existing))
            {
                existing.FlowId = flowId;
                existing.TriggerKey = triggerKey;
                existing.Paused = false;
                if (!string.Equals(existing.EffectiveCron, cronExpression, StringComparison.Ordinal))
                {
                    // A different schedule: retire the chain this process knows about, start the new one.
                    supersededSequence = existing.LastSequenceNumber;
                    existing.LastSequenceNumber = null;
                    existing.EffectiveCron = cronExpression;
                    existing.NextExecution = nextFire;
                    epoch = NextEpochLocked(jobId, nextFire);
                    seed = true;
                }
            }
            else
            {
                _jobs[jobId] = new JobState
                {
                    FlowId = flowId,
                    TriggerKey = triggerKey,
                    EffectiveCron = cronExpression,
                    NextExecution = nextFire,
                };
                seed = true;
                adoptFromInventory = !_startupComplete;
                if (!adoptFromInventory)
                {
                    epoch = NextEpochLocked(jobId, nextFire);
                }
            }
        }

        if (supersededSequence is { } seq)
        {
            _ = TryCancelScheduledAsync(seq);
        }

        if (!seed)
        {
            // Same job, same cron: its chain is alive. Re-seeding here would add a second tick for the same
            // instant, which duplicate detection only absorbs within its window.
            LogRegistrationUnchanged(_logger, Sanitize(jobId));
            return;
        }

        // Fire-and-forget — keeping RegisterOrUpdate synchronous matches the PeriodicTimer dispatcher's
        // signature. ScheduleNextAsync throws on failure so the self-perpetuating consumer can redeliver; for
        // the registration-time call we log and continue, since the next sync re-attempts.
        var seeding = SeedAsync(jobId, flowId, triggerKey, cronExpression, nextFire, epoch, adoptFromInventory);
        if (adoptFromInventory)
        {
            lock (_startupGate)
            {
                _startupSeeds.Add(seeding);
            }
        }

        LogRegistered(_logger, Sanitize(jobId), Sanitize(cronExpression));
    }

    /// <inheritdoc/>
    public void Remove(string jobId)
    {
        if (_jobs.TryRemove(jobId, out var state))
        {
            LogRemoved(_logger, Sanitize(jobId));
            // Best-effort cancellation of the pending tick; a tick this process cannot cancel (sent by a previous
            // process, or already delivered) is dropped by the consumer because the job is no longer registered.
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
        var jobId = RecurringTriggerJobId.Create(flowId, triggerKey);
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

    // ── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Ends the startup phase: waits for the first-registration seeds (and the one queue peek they share) to
    /// finish, then discards the peeked snapshot so later registrations always seed.
    /// </summary>
    /// <returns>A task that completes once every startup seed has been scheduled or adopted.</returns>
    /// <remarks>
    /// The cron consumer calls this before it starts processing. From then on it completes ticks, so the
    /// snapshot could name a tick that no longer exists, and adopting it would leave the job without a chain.
    /// Each seed already handles its own failure; this wait cannot throw.
    /// </remarks>
    internal async Task CompleteStartupAsync()
    {
        Task[] pending;
        lock (_startupGate)
        {
            _startupComplete = true;
            pending = _startupSeeds.ToArray();
            _startupSeeds.Clear();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        lock (_startupGate)
        {
            _inventory = null;
        }
    }

    /// <summary>
    /// Classifies a recurring tick the consumer received against the job's chain, adopting a newer chain when
    /// the tick belongs to one.
    /// </summary>
    /// <param name="envelope">The received recurring tick (non-empty <see cref="CronEnvelope.Cron"/>).</param>
    /// <param name="registerIfMissing">
    /// <see langword="true"/> when the persisted schedule state says the job is active although this process has
    /// no registration for it (it was paused or disabled here and resumed on another replica): the job is then
    /// registered from the tick, without seeding, so the chain the other replica started keeps running.
    /// </param>
    /// <returns>What the consumer must do with the tick.</returns>
    /// <remarks>O(1): one dictionary lookup and a few comparisons, under the hub's gate.</remarks>
    internal TickVerdict ObserveTick(CronEnvelope envelope, bool registerIfMissing)
    {
        lock (_startupGate)
        {
            if (!_jobs.TryGetValue(envelope.JobId, out var state))
            {
                if (!registerIfMissing)
                {
                    return TickVerdict.NotRegistered;
                }

                state = new JobState
                {
                    FlowId = envelope.FlowId,
                    TriggerKey = envelope.TriggerKey,
                    EffectiveCron = envelope.Cron,
                    NextExecution = envelope.ScheduledFor,
                };
                _jobs[envelope.JobId] = state;
            }

            if (_latestEpoch.TryGetValue(envelope.JobId, out var latest))
            {
                if (envelope.ChainEpoch < latest)
                {
                    return TickVerdict.Superseded;
                }

                if (envelope.ChainEpoch == latest
                    && !string.Equals(state.EffectiveCron, envelope.Cron, StringComparison.Ordinal))
                {
                    // Same epoch, different cron: only pre-epoch ticks (epoch unset) can collide like this.
                    // The job's registered cron is the current one.
                    return TickVerdict.Superseded;
                }
            }

            if (!_latestEpoch.TryGetValue(envelope.JobId, out latest) || envelope.ChainEpoch > latest)
            {
                // A newer chain than this process knows — started by another replica (a dashboard edit there)
                // or by a newer deployment. Follow it: later ticks of the older chain are dropped here too.
                _latestEpoch[envelope.JobId] = envelope.ChainEpoch;
                if (!string.Equals(state.EffectiveCron, envelope.Cron, StringComparison.Ordinal))
                {
                    state.EffectiveCron = envelope.Cron;
                    state.LastSequenceNumber = null;
                }
            }

            if (state.LastFiredFor is { } fired && fired >= envelope.ScheduledFor)
            {
                return TickVerdict.Duplicate;
            }

            return TickVerdict.Current;
        }
    }

    /// <summary>
    /// Records observed execution metadata after the cron consumer fires the trigger.
    /// Called by <see cref="ServiceBusCronProcessorHostedService"/>.
    /// </summary>
    /// <param name="jobId">The job that fired.</param>
    /// <param name="state">The outcome shown on the dashboard (<c>Succeeded</c> / <c>Failed</c>).</param>
    /// <param name="firedAt">When the consumer fired it.</param>
    /// <param name="scheduledFor">
    /// The fired tick's scheduled instant, recorded on success so a redelivered copy is not fired again;
    /// <see langword="null"/> for a failure (the redelivery must retry) or a one-off fire.
    /// </param>
    internal void NoteFireResult(string jobId, string state, DateTimeOffset firedAt, DateTimeOffset? scheduledFor = null)
    {
        if (_jobs.TryGetValue(jobId, out var js))
        {
            js.LastExecution = firedAt;
            js.LastJobId = jobId;
            js.LastJobState = state;
            if (scheduledFor is { } tick && (js.LastFiredFor is not { } fired || tick > fired))
            {
                js.LastFiredFor = tick;
            }
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
    /// <see cref="Remove(string)"/> via <see cref="RecurringTriggerSync.SyncTriggersAsync"/>) would otherwise
    /// keep enqueuing scheduled messages every tick even though the engine rejects each fire.
    /// </remarks>
    internal Task ScheduleNextAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        CancellationToken ct)
        => ScheduleNextAsync(jobId, flowId, triggerKey, cron, fireAt, chainEpoch: default, ct);

    /// <summary>
    /// Schedules the next tick of the chain identified by <paramref name="chainEpoch"/>; see
    /// <see cref="ScheduleNextAsync(string, Guid, string, string, DateTimeOffset, CancellationToken)"/>.
    /// </summary>
    /// <param name="jobId">The registered job.</param>
    /// <param name="flowId">The job's flow.</param>
    /// <param name="triggerKey">The job's manifest trigger key.</param>
    /// <param name="cron">The chain's cron, embedded in the tick.</param>
    /// <param name="fireAt">When the tick fires.</param>
    /// <param name="chainEpoch">The chain's epoch, carried unchanged into the tick.</param>
    /// <param name="ct">Cancellation token for the broker call.</param>
    /// <returns>A task that completes once the tick is scheduled, or at once when the job is not registered.</returns>
    internal async Task ScheduleNextAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        DateTimeOffset chainEpoch,
        CancellationToken ct)
    {
        // If the job has been removed (flow disabled / unregistered), do NOT schedule the next fire.
        // Without this check the cron consumer self-perpetuates indefinitely for disabled flows.
        if (!_jobs.ContainsKey(jobId))
        {
            LogScheduleNextSkipped(_logger, Sanitize(jobId));
            return;
        }

        var seq = await EnqueueCronMessageAsync(jobId, flowId, triggerKey, cron, fireAt, ct, chainEpoch).ConfigureAwait(false);
        if (seq is { } s && _jobs.TryGetValue(jobId, out var js)
            && string.Equals(js.EffectiveCron, cron, StringComparison.Ordinal))
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
    /// <param name="chainEpoch">The epoch of the chain the tick belongs to; <see langword="default"/> for a one-off fire.</param>
    /// <returns>A fully-populated message ready to hand to <c>ScheduleMessageAsync</c>.</returns>
    internal static ServiceBusMessage BuildCronMessage(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        DateTimeOffset now,
        DateTimeOffset chainEpoch = default)
    {
        var envelope = new CronEnvelope
        {
            FlowId = flowId,
            TriggerKey = triggerKey,
            Cron = cron,
            ScheduledFor = fireAt,
            JobId = jobId,
            ChainEpoch = chainEpoch,
        };
        var msg = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(envelope))
        {
            // Deterministic id, so the same tick of the same chain scheduled twice (a redelivered tick
            // perpetuated again, replicas seeding the same chain at once) collapses under duplicate detection.
            // A recurring tick's id carries its chain epoch: without it a new chain whose first tick falls on
            // the same instant as the replaced chain's pending tick (a cron change to "*/2" while "* * * * *"
            // has 12:20 scheduled) got the same id, the broker discarded it as a duplicate, and once the old
            // tick was cancelled or dropped the job had no chain left. One-off fires keep the plain form.
            MessageId = string.IsNullOrEmpty(cron)
                ? $"{flowId}:{triggerKey}:{fireAt.UtcDateTime:O}"
                : $"{flowId}:{triggerKey}:{fireAt.UtcDateTime:O}:{chainEpoch.UtcTicks}",
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

    /// <summary>Computes the next occurrence of <paramref name="cron"/> after <paramref name="baseUtc"/>.</summary>
    /// <param name="cron">A 5-field (minute) or 6-field (second) cron expression.</param>
    /// <param name="baseUtc">The instant to search from.</param>
    /// <returns>The next occurrence, or <see cref="DateTimeOffset.MaxValue"/> when there is none.</returns>
    /// <exception cref="CronFormatException">Thrown when <paramref name="cron"/> does not parse.</exception>
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

    private static DateTimeOffset ParseAndComputeNext(string cronExpression, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            throw new ArgumentException("A cron expression is required.", nameof(cronExpression));
        }

        try
        {
            return ComputeNext(cronExpression, now);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException(
                $"'{Sanitize(cronExpression)}' is not a valid cron expression: {ex.Message}", nameof(cronExpression), ex);
        }
    }

    /// <summary>
    /// Seeds the first tick of a job's chain — unless, for a startup registration, the previous process left a
    /// tick for the same job and cron on the queue, in which case that tick is adopted as the chain.
    /// </summary>
    private async Task SeedAsync(
        string jobId, Guid flowId, string triggerKey, string cron, DateTimeOffset nextFire, DateTimeOffset epoch, bool consultInventory)
    {
        try
        {
            if (consultInventory)
            {
                if (await TryAdoptSurvivingTickAsync(jobId, cron).ConfigureAwait(false))
                {
                    return;
                }

                lock (_startupGate)
                {
                    epoch = NextEpochLocked(jobId, nextFire);
                }
            }

            await ScheduleNextAsync(jobId, flowId, triggerKey, cron, nextFire, epoch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSeedFailed(_logger, ex, Sanitize(jobId), nextFire);
        }
    }

    /// <summary>
    /// Adopts a tick of <paramref name="jobId"/>'s chain that survived the previous process, cancelling any of its
    /// ticks scheduled under a different cron.
    /// </summary>
    /// <returns><see langword="true"/> when a surviving tick on <paramref name="cron"/> was adopted.</returns>
    private async Task<bool> TryAdoptSurvivingTickAsync(string jobId, string cron)
    {
        var inventory = await GetInventoryAsync().ConfigureAwait(false);
        List<PendingTick>? ticks;
        lock (_startupGate)
        {
            if (!inventory.Remove(jobId, out ticks))
            {
                return false;
            }
        }

        // The chain to keep: the newest one still on the job's cron. Every other tick belongs to a replaced
        // schedule (cron changed while the host was down) or is a surplus copy.
        PendingTick? adopted = null;
        var newestEpoch = DateTimeOffset.MinValue;
        foreach (var tick in ticks)
        {
            if (tick.ChainEpoch > newestEpoch)
            {
                newestEpoch = tick.ChainEpoch;
            }

            if (string.Equals(tick.Cron, cron, StringComparison.Ordinal)
                && (adopted is not { } best || tick.ChainEpoch > best.ChainEpoch))
            {
                adopted = tick;
            }
        }

        lock (_startupGate)
        {
            // Remember the newest epoch on the queue so a chain seeded below outranks every straggler.
            if (!_latestEpoch.TryGetValue(jobId, out var known) || newestEpoch > known)
            {
                _latestEpoch[jobId] = newestEpoch;
            }

            if (adopted is { } chosen && chosen.ChainEpoch >= _latestEpoch[jobId])
            {
                _latestEpoch[jobId] = chosen.ChainEpoch;
            }
            else
            {
                adopted = null;
            }
        }

        // Only a still-scheduled message can be cancelled; an active one is dropped by the consumer.
        // Startup-only (once per job, at its first registration), so the filter reads clearer as a Where.
        var keepSequence = adopted?.SequenceNumber;
        foreach (var tick in ticks.Where(t => t.IsScheduled && t.SequenceNumber != keepSequence))
        {
            await TryCancelScheduledAsync(tick.SequenceNumber).ConfigureAwait(false);
        }

        if (adopted is not { } kept)
        {
            return false;
        }

        if (_jobs.TryGetValue(jobId, out var js) && string.Equals(js.EffectiveCron, cron, StringComparison.Ordinal))
        {
            js.NextExecution = kept.ScheduledFor;
            js.LastSequenceNumber = kept.IsScheduled ? kept.SequenceNumber : null;
        }

        LogAdoptedSurvivingTick(_logger, Sanitize(jobId), kept.ScheduledFor);
        return true;
    }

    private Task<Dictionary<string, List<PendingTick>>> GetInventoryAsync()
    {
        lock (_startupGate)
        {
            return _inventory ??= LoadInventoryAsync();
        }
    }

    /// <summary>
    /// Peeks the cron queue once and groups the recurring ticks on it by job id. Peeking returns scheduled
    /// messages (and locked or deferred ones) without changing them.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="MaxInventoryMessages"/> and <see cref="InventoryTimeout"/>. Any failure yields what
    /// was read so far: a tick the peek missed is seeded again, and the consumer drops the second copy of a
    /// tick that has already fired.
    /// </remarks>
    private async Task<Dictionary<string, List<PendingTick>>> LoadInventoryAsync()
    {
        var inventory = new Dictionary<string, List<PendingTick>>(StringComparer.Ordinal);
        ServiceBusReceiver? receiver = null;
        try
        {
            receiver = _client.CreateReceiver(_options.CronQueueName);
            if (receiver is null)
            {
                return inventory;
            }

            using var timeout = new CancellationTokenSource(InventoryTimeout);
            long? from = null;
            var seen = 0;
            while (seen < MaxInventoryMessages)
            {
                var page = await receiver.PeekMessagesAsync(PeekPageSize, from, timeout.Token).ConfigureAwait(false);
                if (page is null || page.Count == 0)
                {
                    break;
                }

                foreach (var message in page)
                {
                    seen++;
                    AddToInventory(inventory, message);
                }

                from = page[^1].SequenceNumber + 1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogInventoryFailed(_logger, ex, _options.CronQueueName);
        }
        finally
        {
            if (receiver is not null)
            {
                try { await receiver.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { LogInventoryFailed(_logger, ex, _options.CronQueueName); }
            }
        }

        return inventory;
    }

    private static void AddToInventory(Dictionary<string, List<PendingTick>> inventory, ServiceBusReceivedMessage message)
    {
        CronEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CronEnvelope>(message.Body.ToMemory().Span);
        }
        catch (JsonException)
        {
            return;  // the consumer dead-letters it
        }

        // One-off fires (empty cron) are not part of any chain.
        if (envelope is null || string.IsNullOrEmpty(envelope.Cron) || string.IsNullOrEmpty(envelope.JobId))
        {
            return;
        }

        if (!inventory.TryGetValue(envelope.JobId, out var ticks))
        {
            ticks = new List<PendingTick>(1);
            inventory[envelope.JobId] = ticks;
        }

        ticks.Add(new PendingTick(
            envelope.Cron,
            envelope.ScheduledFor,
            envelope.ChainEpoch,
            message.SequenceNumber,
            message.State == ServiceBusMessageState.Scheduled));
    }

    private async Task<long?> EnqueueCronMessageAsync(
        string jobId,
        Guid flowId,
        string triggerKey,
        string cron,
        DateTimeOffset fireAt,
        CancellationToken ct,
        DateTimeOffset chainEpoch = default)
    {
        var msg = BuildCronMessage(jobId, flowId, triggerKey, cron, fireAt, _timeProvider.GetUtcNow(), chainEpoch);

        try
        {
            // Use ScheduleMessageAsync to get back a sequence number for later cancellation.
            var seq = await _sender.Value.ScheduleMessageAsync(msg, fireAt, ct).ConfigureAwait(false);
            return seq;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            LogCronQueueNotFound(_logger, ex, _options.CronQueueName);
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
            LogCancelFailed(_logger, ex, sequenceNumber);
        }
    }

    /// <summary>
    /// Assigns the epoch of a chain about to be seeded: its first tick's instant, raised above every epoch
    /// already seen for the job. Caller holds <see cref="_startupGate"/>.
    /// </summary>
    /// <remarks>
    /// The first tick's instant (not the wall clock) makes the epoch deterministic: replicas seeding the same
    /// job at the same moment compute the same epoch and the same message id, so duplicate detection keeps one
    /// message and no replica treats the survivor as an older chain.
    /// </remarks>
    private DateTimeOffset NextEpochLocked(string jobId, DateTimeOffset firstFire)
    {
        var epoch = firstFire;
        if (_latestEpoch.TryGetValue(jobId, out var latest) && latest >= epoch)
        {
            epoch = latest.AddTicks(1);
        }

        _latestEpoch[jobId] = epoch;
        return epoch;
    }

    // Job ids and cron expressions can arrive from dashboard URLs and bodies; strip line breaks so they
    // cannot forge log entries. Cold path (registration, failures), so the chained Replace is fine.
    private static string Sanitize(string value) => value.Replace('\r', '_').Replace('\n', '_');

    [LoggerMessage(EventId = 3140, Level = LogLevel.Information, Message = "Registered recurring job {JobId} (cron='{Cron}').")]
    private static partial void LogRegistered(ILogger logger, string jobId, string cron);

    [LoggerMessage(EventId = 3141, Level = LogLevel.Debug, Message = "Recurring job {JobId} re-registered with its current cron; its chain is alive, nothing scheduled.")]
    private static partial void LogRegistrationUnchanged(ILogger logger, string jobId);

    [LoggerMessage(EventId = 3142, Level = LogLevel.Information, Message = "Removed recurring job {JobId}.")]
    private static partial void LogRemoved(ILogger logger, string jobId);

    [LoggerMessage(EventId = 3143, Level = LogLevel.Error, Message = "Failed to schedule first firing for {JobId} at {FireAt:O}.")]
    private static partial void LogSeedFailed(ILogger logger, Exception ex, string jobId, DateTimeOffset fireAt);

    [LoggerMessage(EventId = 3144, Level = LogLevel.Information, Message = "Recurring job {JobId}: adopted the tick for {ScheduledFor:O} left on the cron queue by the previous process instead of scheduling a second one.")]
    private static partial void LogAdoptedSurvivingTick(ILogger logger, string jobId, DateTimeOffset scheduledFor);

    [LoggerMessage(EventId = 3145, Level = LogLevel.Warning, Message = "Could not read the pending ticks on cron queue '{Queue}'; every job seeds a fresh tick (a duplicate of a surviving one is dropped by the consumer).")]
    private static partial void LogInventoryFailed(ILogger logger, Exception ex, string queue);

    [LoggerMessage(EventId = 3146, Level = LogLevel.Debug, Message = "Skipping ScheduleNextAsync for {JobId}: job is no longer registered (flow disabled or removed).")]
    private static partial void LogScheduleNextSkipped(ILogger logger, string jobId);

    [LoggerMessage(EventId = 3147, Level = LogLevel.Error, Message = "Cron queue '{Queue}' not found. Ensure topology is provisioned.")]
    private static partial void LogCronQueueNotFound(ILogger logger, Exception ex, string queue);

    [LoggerMessage(EventId = 3148, Level = LogLevel.Debug, Message = "Could not cancel scheduled message {Seq} (already delivered or queue gone).")]
    private static partial void LogCancelFailed(ILogger logger, Exception ex, long seq);

    /// <summary>A recurring tick found on the cron queue by the startup peek.</summary>
    /// <param name="Cron">The cron its chain was scheduled with.</param>
    /// <param name="ScheduledFor">The instant it fires.</param>
    /// <param name="ChainEpoch">The epoch of the chain it belongs to.</param>
    /// <param name="SequenceNumber">Its broker sequence number (cancellation handle while still scheduled).</param>
    /// <param name="IsScheduled">Whether it is still scheduled (cancellable) rather than already due.</param>
    private readonly record struct PendingTick(string Cron, DateTimeOffset ScheduledFor, DateTimeOffset ChainEpoch, long SequenceNumber, bool IsScheduled);

    /// <summary>What the cron consumer must do with a recurring tick (<see cref="ObserveTick"/>).</summary>
    internal enum TickVerdict
    {
        /// <summary>The tick belongs to the job's current chain and has not fired yet: fire it and perpetuate.</summary>
        Current,

        /// <summary>The job is not registered in this process (paused, disabled or removed): drop the tick.</summary>
        NotRegistered,

        /// <summary>The tick belongs to an older chain the job has replaced: drop it, which ends that chain.</summary>
        Superseded,

        /// <summary>The same tick already fired (a redelivery or a second copy): drop it.</summary>
        Duplicate,
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
        public DateTimeOffset? LastFiredFor;
    }
}
