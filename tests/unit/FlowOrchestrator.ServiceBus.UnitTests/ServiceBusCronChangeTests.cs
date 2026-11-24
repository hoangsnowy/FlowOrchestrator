using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Changing a Service Bus cron schedule while its chain is running, replayed minute by minute against an
/// in-test cron queue that applies the broker's duplicate detection.
/// </summary>
/// <remarks>
/// <para>
/// Reproduces the 2.0 stress-run failure (<c>s3-ms-sb-cron</c>): a job on <c>* * * * *</c> fired every minute,
/// the dashboard switched it to <c>*/2 * * * *</c> at 12:19:19 — after the 12:20 tick of the old chain had
/// already been scheduled — and the job never fired again until a pause/resume.
/// </para>
/// <para>
/// The new chain's first tick fell on the same instant as the old chain's pending tick, and cron message ids
/// were <c>{flowId}:{triggerKey}:{fireAt}</c> only, so the broker's duplicate detection discarded the new
/// chain's first tick as a repeat of the old one. The old tick was then cancelled (or dropped by the consumer
/// as an older chain), and no chain was left.
/// </para>
/// </remarks>
public sealed class ServiceBusCronChangeTests
{
    private const string EveryMinute = "* * * * *";
    private const string EveryTwoMinutes = "*/2 * * * *";
    private const string EveryThreeMinutes = "*/3 * * * *";

    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 15, 30, TimeSpan.Zero);

    private readonly SettableClock _clock = new(Start);
    private readonly FakeCronQueue _queue;
    private readonly ServiceBusClient _client = Substitute.For<ServiceBusClient>();
    private readonly ServiceBusReceiver _completions = Substitute.For<ServiceBusReceiver>();
    private readonly List<DateTimeOffset> _fires = new();
    private readonly Guid _flowId = Guid.Parse("5f0e0000-0000-4000-8002-000000000003");
    private readonly InMemoryFlowScheduleStateStore _scheduleState = new();

    /// <summary>Wires the client to the in-test cron queue (10-minute duplicate-detection window, as production).</summary>
    public ServiceBusCronChangeTests()
    {
        _queue = new FakeCronQueue(_clock, TimeSpan.FromMinutes(10));
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_queue.Schedule(call.Arg<ServiceBusMessage>(), call.Arg<DateTimeOffset>())));
        sender.CancelScheduledMessageAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _queue.Cancel(call.Arg<long>());
                return Task.CompletedTask;
            });
        _client.CreateSender(Arg.Any<string>()).Returns(sender);
        _client.CreateReceiver(Arg.Any<string>()).Returns(_ => _queue.CreatePeekReceiver());
    }

    private string JobId => RecurringTriggerJobId.Create(_flowId, "tick");

    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records the instant of every run the consumer starts.</summary>
    private sealed class RecordingRunner(Func<DateTimeOffset> now, List<DateTimeOffset> fires) : IServiceBusFlowRunner
    {
        public ValueTask RunStepAsync(StepEnvelope envelope, ServiceBusReceivedMessage message, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask TriggerByScheduleAsync(Guid flowId, string triggerKey, string messageId, CancellationToken ct)
        {
            fires.Add(now());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CronFlow(Guid id) : IFlowDefinition
    {
        public Guid Id { get; } = id;
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; } = new()
        {
            Triggers =
            {
                ["tick"] = new TriggerMetadata
                {
                    Type = TriggerType.Cron,
                    Inputs = new Dictionary<string, object?> { ["cronExpression"] = EveryMinute },
                },
            },
        };
    }

    /// <summary>
    /// A cron queue with scheduled delivery, cancellation, peek and duplicate detection: a message whose id was
    /// accepted within the window is discarded (the sender still gets a sequence number, as from the broker).
    /// </summary>
    private sealed class FakeCronQueue(SettableClock clock, TimeSpan window)
    {
        private readonly Dictionary<string, DateTimeOffset> _acceptedIds = new(StringComparer.Ordinal);
        private readonly List<(long Sequence, CronEnvelope Tick, DateTimeOffset Due)> _pending = new();
        private long _sequence;

        public long Schedule(ServiceBusMessage message, DateTimeOffset due)
        {
            var sequence = ++_sequence;
            if (_acceptedIds.TryGetValue(message.MessageId, out var acceptedAt) && clock.Now - acceptedAt < window)
            {
                return sequence; // duplicate detection: silently discarded
            }

            _acceptedIds[message.MessageId] = clock.Now;
            _pending.Add((sequence, JsonSerializer.Deserialize<CronEnvelope>(message.Body.ToArray())!, due));
            return sequence;
        }

        public void Cancel(long sequence) => _pending.RemoveAll(p => p.Sequence == sequence);

        public bool TryTakeNextDue(DateTimeOffset until, out CronEnvelope tick, out DateTimeOffset due)
        {
            var next = _pending.Where(p => p.Due <= until).OrderBy(p => p.Due).ThenBy(p => p.Sequence).FirstOrDefault();
            if (next.Tick is null)
            {
                tick = null!;
                due = default;
                return false;
            }

            _pending.Remove(next);
            tick = next.Tick;
            due = next.Due;
            return true;
        }

        public ServiceBusReceiver CreatePeekReceiver()
        {
            var peeked = _pending
                .Select(p => ServiceBusModelFactory.ServiceBusReceivedMessage(
                    body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(p.Tick)),
                    sequenceNumber: p.Sequence,
                    serviceBusMessageState: ServiceBusMessageState.Scheduled))
                .ToList();
            var receiver = Substitute.For<ServiceBusReceiver>();
            receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(peeked),
                    Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Array.Empty<ServiceBusReceivedMessage>()));
            return receiver;
        }
    }

    /// <summary>One process: its hub and its cron consumer, sharing the flow store and schedule state.</summary>
    private sealed record Replica(ServiceBusRecurringTriggerHub Hub, ServiceBusCronProcessorHostedService Consumer);

    private async Task<Replica> StartReplicaAsync()
    {
        var hub = new ServiceBusRecurringTriggerHub(_client, new ServiceBusRuntimeOptions(), NullLogger<ServiceBusRecurringTriggerHub>.Instance, _clock);
        var flowStore = Substitute.For<IFlowStore>();
        flowStore.GetByIdAsync(_flowId).Returns(new FlowDefinitionRecord { Id = _flowId, IsEnabled = true });
        var flows = new FlowRegistry([new CronFlow(_flowId)]);
        var services = new ServiceCollection()
            .AddSingleton<IServiceBusFlowRunner>(new RecordingRunner(() => _clock.Now, _fires))
            .AddSingleton(flows)
            .AddSingleton(flowStore)
            .AddSingleton<IFlowScheduleStateStore>(_scheduleState)
            .AddSingleton(new FlowSchedulerOptions { PersistOverrides = true })
            .BuildServiceProvider();

        // The startup sync, exactly as FlowSyncHostedService runs it, then the consumer's start-up gate.
        await new RecurringTriggerSync(flows, hub, _scheduleState, new FlowSchedulerOptions { PersistOverrides = true })
            .SyncTriggersAsync(_flowId, isEnabled: true);
        await hub.CompleteStartupAsync();

        var consumer = new ServiceBusCronProcessorHostedService(
            _client,
            new ServiceBusRuntimeOptions(),
            hub,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ServiceBusCronProcessorHostedService>.Instance,
            _clock);
        return new Replica(hub, consumer);
    }

    /// <summary>The dashboard's <c>PUT /api/schedules/{jobId}/cron</c>: schedule on the receiving replica, then persist.</summary>
    private async Task PutCronAsync(Replica replica, string cron, DateTimeOffset at)
    {
        _clock.Now = at;
        replica.Hub.RegisterOrUpdate(JobId, _flowId, "tick", cron);
        await _scheduleState.SaveAsync(new FlowScheduleState
        {
            JobId = JobId,
            FlowId = _flowId,
            FlowName = "StressCronFlow",
            TriggerKey = "tick",
            CronOverride = cron,
            UpdatedAtUtc = at,
        });
    }

    /// <summary>Delivers every tick due up to <paramref name="until"/>, 200 ms late, round-robin over <paramref name="replicas"/>.</summary>
    private async Task RunUntilAsync(DateTimeOffset until, params Replica[] replicas)
    {
        var next = 0;
        while (_queue.TryTakeNextDue(until, out var tick, out var due))
        {
            _clock.Now = due.AddMilliseconds(200);
            var consumer = replicas[next++ % replicas.Length].Consumer;
            var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(tick)),
                messageId: Guid.NewGuid().ToString("N"));
            var onMessage = typeof(ServiceBusCronProcessorHostedService)
                .GetMethod("OnMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)onMessage.Invoke(consumer, [new ProcessMessageEventArgs(message, _completions, CancellationToken.None)])!;
        }

        _clock.Now = until;
    }

    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 11, 24, hour, minute, second, TimeSpan.Zero);

    private string FiredMinutes() => string.Join(",", _fires.Select(f => f.ToString("HH:mm")));

    [Fact]
    public async Task CronChangedWhileTheOldChainsNextTickIsScheduled_NewScheduleKeepsFiring()
    {
        // Arrange — the stress-run timeline: every minute from 12:16, PUT */2 at 12:19:19 (12:20 already scheduled).
        var replica = await StartReplicaAsync();
        await RunUntilAsync(At(12, 19, 19), replica);

        // Act
        await PutCronAsync(replica, EveryTwoMinutes, At(12, 19, 19));
        await RunUntilAsync(At(12, 25), replica);

        // Assert — 12:20 once (the new chain), then every two minutes; no gap, no double fire.
        Assert.Equal("12:16,12:17,12:18,12:19,12:20,12:22,12:24", FiredMinutes());
    }

    [Fact]
    public async Task CronChangedOnOneOfThreeReplicas_NewScheduleKeepsFiringEverywhere()
    {
        // Arrange — the stress run had three replicas. The ticks so far were consumed (and the 12:20 tick
        // scheduled) by the other two, so the replica taking the PUT cannot cancel that pending tick.
        var replicas = new[] { await StartReplicaAsync(), await StartReplicaAsync(), await StartReplicaAsync() };
        await RunUntilAsync(At(12, 19, 19), replicas[1], replicas[2]);

        // Act — every later tick is delivered to the two replicas that did NOT take the PUT.
        await PutCronAsync(replicas[0], EveryTwoMinutes, At(12, 19, 19));
        await RunUntilAsync(At(12, 25), replicas[1], replicas[2]);

        // Assert — the old chain's ticks are not fired (and perpetuated) where the change was never seen.
        Assert.Equal("12:16,12:17,12:18,12:19,12:20,12:22,12:24", FiredMinutes());
    }

    [Fact]
    public async Task CronChangedTwiceQuickly_OnlyTheLastScheduleFires()
    {
        // Arrange
        var replica = await StartReplicaAsync();
        await RunUntilAsync(At(12, 19, 19), replica);

        // Act — */2 then, 21 s later and before either takes effect, */3.
        await PutCronAsync(replica, EveryTwoMinutes, At(12, 19, 19));
        await PutCronAsync(replica, EveryThreeMinutes, At(12, 19, 40));
        await RunUntilAsync(At(12, 28), replica);

        // Assert
        Assert.Equal("12:16,12:17,12:18,12:19,12:21,12:24,12:27", FiredMinutes());
    }

    [Fact]
    public async Task CronChangedThenHostRestarted_NewScheduleKeepsFiringOnce()
    {
        // Arrange
        var before = await StartReplicaAsync();
        await RunUntilAsync(At(12, 19, 19), before);
        await PutCronAsync(before, EveryTwoMinutes, At(12, 19, 19));

        // Act — the process is replaced before the change takes effect; the new one syncs the persisted override.
        _clock.Now = At(12, 19, 45);
        var after = await StartReplicaAsync();
        await RunUntilAsync(At(12, 25), after);

        // Assert
        Assert.Equal("12:16,12:17,12:18,12:19,12:20,12:22,12:24", FiredMinutes());
    }

    [Fact]
    public async Task CronChangedAndChangedBack_OriginalScheduleResumesWithoutDoubleFires()
    {
        // Arrange
        var replica = await StartReplicaAsync();
        await RunUntilAsync(At(12, 19, 19), replica);

        // Act — */2 at 12:19:19 (fires 12:20), back to every minute at 12:20:30.
        await PutCronAsync(replica, EveryTwoMinutes, At(12, 19, 19));
        await RunUntilAsync(At(12, 20, 30), replica);
        await PutCronAsync(replica, EveryMinute, At(12, 20, 30));
        await RunUntilAsync(At(12, 25), replica);

        // Assert
        Assert.Equal("12:16,12:17,12:18,12:19,12:20,12:21,12:22,12:23,12:24,12:25", FiredMinutes());
    }
}
