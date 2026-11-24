using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// One chain per job: restart adoption, chain epochs across replicas, persisted pause state, duplicate ticks,
/// and the start-up ordering of the Service Bus cron consumer.
/// </summary>
public sealed class ServiceBusCronChainEpochTests
{
    private const string Cron = "0 * * * *";        // hourly — far longer than the 10-minute dedup window
    private const string OtherCron = "*/5 * * * *";

    private readonly ServiceBusClient _client = Substitute.For<ServiceBusClient>();
    private readonly ServiceBusSender _sender = Substitute.For<ServiceBusSender>();
    private readonly ServiceBusReceiver _receiver = Substitute.For<ServiceBusReceiver>();
    private readonly RecordingRunner _runner = new();
    private readonly List<CronEnvelope> _scheduled = new();
    private readonly List<long> _cancelled = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 11, 1, 10, 0, 30, TimeSpan.Zero));
    private readonly Guid _flowId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private long _sequence = 100;

    /// <summary>Routes scheduled and cancelled cron messages to <see cref="_scheduled"/> / <see cref="_cancelled"/>.</summary>
    public ServiceBusCronChainEpochTests()
    {
        _client.CreateSender(Arg.Any<string>()).Returns(_sender);
        _sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (_scheduled)
                {
                    _scheduled.Add(JsonSerializer.Deserialize<CronEnvelope>(call.Arg<ServiceBusMessage>().Body.ToArray())!);
                }

                return Task.FromResult(Interlocked.Increment(ref _sequence));
            });
        _sender.CancelScheduledMessageAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (_cancelled)
                {
                    _cancelled.Add(call.Arg<long>());
                }

                return Task.CompletedTask;
            });
    }

    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingRunner : IServiceBusFlowRunner
    {
        public int TriggerCount;

        public ValueTask RunStepAsync(StepEnvelope envelope, ServiceBusReceivedMessage message, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask TriggerByScheduleAsync(Guid flowId, string triggerKey, string messageId, CancellationToken ct)
        {
            Interlocked.Increment(ref TriggerCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CronFlow : IFlowDefinition
    {
        public CronFlow(Guid id)
        {
            Id = id;
            Manifest = new FlowManifest();
            Manifest.Triggers["tick"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = Cron },
            };
        }

        public Guid Id { get; }
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; }
    }

    private string JobId => RecurringTriggerJobId.Create(_flowId, "tick");

    private ServiceBusRecurringTriggerHub CreateHub() =>
        new(_client, new ServiceBusRuntimeOptions(), NullLogger<ServiceBusRecurringTriggerHub>.Instance, _clock);

    private ServiceBusCronProcessorHostedService CreateConsumer(ServiceBusRecurringTriggerHub hub, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection().AddSingleton<IServiceBusFlowRunner>(_runner);
        services?.Invoke(collection);
        var provider = collection.BuildServiceProvider();
        return new ServiceBusCronProcessorHostedService(
            _client,
            new ServiceBusRuntimeOptions(),
            hub,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ServiceBusCronProcessorHostedService>.Instance,
            _clock);
    }

    /// <summary>Registers the persisted, multi-replica view of the schedule: registry, flow record, pause state.</summary>
    private Action<IServiceCollection> SharedState(bool paused)
    {
        var flowStore = Substitute.For<IFlowStore>();
        flowStore.GetByIdAsync(_flowId).Returns(new FlowDefinitionRecord { Id = _flowId, IsEnabled = true });
        var stateStore = new InMemoryFlowScheduleStateStore();
        stateStore.SaveAsync(new FlowScheduleState { JobId = JobId, FlowId = _flowId, TriggerKey = "tick", IsPaused = paused })
            .GetAwaiter().GetResult();
        return services => services
            .AddSingleton(new FlowRegistry([new CronFlow(_flowId)]))
            .AddSingleton(flowStore)
            .AddSingleton<IFlowScheduleStateStore>(stateStore)
            .AddSingleton(new FlowSchedulerOptions { PersistOverrides = true });
    }

    private async Task DeliverAsync(ServiceBusCronProcessorHostedService consumer, CronEnvelope envelope)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(envelope)),
            messageId: $"{envelope.FlowId}:{envelope.TriggerKey}:{envelope.ScheduledFor.UtcDateTime:O}");
        var args = new ProcessMessageEventArgs(message, _receiver, CancellationToken.None);
        var onMessage = typeof(ServiceBusCronProcessorHostedService)
            .GetMethod("OnMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnMessageAsync not found");
        await (Task)onMessage.Invoke(consumer, [args])!;
    }

    private CronEnvelope Tick(string cron, DateTimeOffset scheduledFor, DateTimeOffset epoch) => new()
    {
        FlowId = _flowId,
        TriggerKey = "tick",
        Cron = cron,
        ScheduledFor = scheduledFor,
        JobId = JobId,
        ChainEpoch = epoch,
    };

    /// <summary>Makes the startup peek of the cron queue return <paramref name="ticks"/> (sequence number, state).</summary>
    private void QueueHolds(params (CronEnvelope Tick, long Sequence, ServiceBusMessageState State)[] ticks)
    {
        var peeked = ticks
            .Select(t => ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(t.Tick)),
                sequenceNumber: t.Sequence,
                serviceBusMessageState: t.State))
            .ToList();
        var peekReceiver = Substitute.For<ServiceBusReceiver>();
        peekReceiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(peeked),
                Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Array.Empty<ServiceBusReceivedMessage>()));
        _client.CreateReceiver(Arg.Any<string>()).Returns(peekReceiver);
    }

    private List<CronEnvelope> Scheduled()
    {
        lock (_scheduled) { return _scheduled.ToList(); }
    }

    [Fact]
    public async Task Restart_TickOfTheSameCronSurvivesOnTheQueue_IsAdoptedInsteadOfSeedingASecondOne()
    {
        // Arrange — the previous process left the 11:00 tick of the hourly chain; its send is > 10 min old,
        // so broker duplicate detection would NOT absorb a second copy.
        var epoch = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var surviving = Tick(Cron, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), epoch);
        QueueHolds((surviving, 42, ServiceBusMessageState.Scheduled));
        var hub = CreateHub();

        // Act — the startup sync registers the job, then the consumer ends the startup phase.
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        await hub.CompleteStartupAsync();

        // Assert — nothing new was scheduled and nothing was cancelled: the surviving tick IS the chain.
        Assert.Empty(Scheduled());
        Assert.Empty(_cancelled);
    }

    [Fact]
    public async Task Restart_AdoptedTick_FiresOnceAndPerpetuatesItsOwnChain()
    {
        // Arrange
        var epoch = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var surviving = Tick(Cron, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), epoch);
        QueueHolds((surviving, 42, ServiceBusMessageState.Scheduled));
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        await hub.CompleteStartupAsync();
        var consumer = CreateConsumer(hub);
        _clock.Now = surviving.ScheduledFor.AddMilliseconds(200);

        // Act
        await DeliverAsync(consumer, surviving);

        // Assert — one run, and the successor carries the adopted chain's epoch.
        Assert.Equal(1, _runner.TriggerCount);
        var next = Assert.Single(Scheduled());
        Assert.Equal(epoch, next.ChainEpoch);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), next.ScheduledFor);
    }

    [Fact]
    public async Task Restart_SurvivingTickHasAnotherCron_IsCancelledAndANewerChainIsSeeded()
    {
        // Arrange — the cron override changed while the host was down.
        var oldEpoch = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var surviving = Tick(OtherCron, new DateTimeOffset(2026, 11, 1, 10, 5, 0, TimeSpan.Zero), oldEpoch);
        QueueHolds((surviving, 7, ServiceBusMessageState.Scheduled));
        var hub = CreateHub();

        // Act
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        await hub.CompleteStartupAsync();

        // Assert
        Assert.Contains(7L, _cancelled);
        var seeded = Assert.Single(Scheduled());
        Assert.Equal(Cron, seeded.Cron);
        Assert.True(seeded.ChainEpoch > oldEpoch);
    }

    [Fact]
    public void RegisterOrUpdate_SameCronAgain_SchedulesNothing()
    {
        // Arrange
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        var first = Assert.Single(Scheduled());

        // Act — a re-sync, or a dashboard save that did not change the expression.
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);

        // Assert — the chain is alive; a second tick for the same instant would fire twice after 10 minutes.
        Assert.Single(Scheduled());
        Assert.Equal(first.ScheduledFor, hub.TryGetJob(JobId, out var job) ? job.NextExecution : default);
    }

    [Fact]
    public void RegisterOrUpdate_UnparseableCron_ThrowsArgumentExceptionAndKeepsTheChain()
    {
        // Arrange
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);

        // Act
        var failure = Record.Exception(() => hub.RegisterOrUpdate(JobId, _flowId, "tick", "every day at noon"));

        // Assert
        Assert.IsType<ArgumentException>(failure);
        Assert.True(hub.TryGetJob(JobId, out var job));
        Assert.Equal(Cron, job.EffectiveCron);
        Assert.Single(Scheduled());
        Assert.Empty(_cancelled);
    }

    [Fact]
    public async Task SameTickDeliveredTwice_FiresOnce()
    {
        // Arrange
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        var tick = Assert.Single(Scheduled());
        var consumer = CreateConsumer(hub);
        _clock.Now = tick.ScheduledFor.AddMilliseconds(200);

        // Act — the same tick arrives twice (lock lost after the run started, or two copies on the queue).
        await DeliverAsync(consumer, tick);
        await DeliverAsync(consumer, tick);

        // Assert
        Assert.Equal(1, _runner.TriggerCount);
    }

    [Fact]
    public async Task TickOfANewerChainStartedElsewhere_IsFollowedAndTheOlderChainIsDropped()
    {
        // Arrange — this replica runs the */5 chain; another replica switched the job to hourly.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", OtherCron);
        var localTick = Assert.Single(Scheduled());
        var newerChain = Tick(Cron, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), localTick.ChainEpoch.AddMinutes(30));
        var consumer = CreateConsumer(hub);

        // Act — the newer chain's tick lands here first, then this replica's own older tick.
        _clock.Now = newerChain.ScheduledFor.AddMilliseconds(200);
        await DeliverAsync(consumer, newerChain);
        var afterNewer = _runner.TriggerCount;
        await DeliverAsync(consumer, localTick);

        // Assert — the newer chain fired and perpetuated; the older one ended here.
        Assert.Equal(1, afterNewer);
        Assert.Equal(1, _runner.TriggerCount);
        Assert.Equal(Cron, hub.TryGetJob(JobId, out var job) ? job.EffectiveCron : null);
        Assert.DoesNotContain(Scheduled().Skip(1), e => e.Cron == OtherCron);
    }

    [Fact]
    public async Task PausedInTheSharedState_TickIsDroppedEvenWhereTheJobIsStillRegistered()
    {
        // Arrange — the pause was made on another replica: this replica still has the job registered.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        var tick = Assert.Single(Scheduled());
        var consumer = CreateConsumer(hub, SharedState(paused: true));
        _clock.Now = tick.ScheduledFor.AddMilliseconds(200);

        // Act
        await DeliverAsync(consumer, tick);

        // Assert — no run, no successor, and this replica no longer holds the job.
        Assert.Equal(0, _runner.TriggerCount);
        Assert.Single(Scheduled());
        Assert.False(hub.TryGetJob(JobId, out _));
    }

    [Fact]
    public async Task PausedElsewhereThenResumedHere_SeedsANewChain()
    {
        // Arrange — the tick of the paused job was dropped here, which ended its chain.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        var tick = Assert.Single(Scheduled());
        var consumer = CreateConsumer(hub, SharedState(paused: true));
        _clock.Now = tick.ScheduledFor.AddMilliseconds(200);
        await DeliverAsync(consumer, tick);

        // Act — the dashboard resume lands on this replica.
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);

        // Assert — a new chain, ranked above the one the pause ended.
        Assert.Equal(2, Scheduled().Count);
        var resumed = Scheduled()[1];
        Assert.True(resumed.ChainEpoch > tick.ChainEpoch);
    }

    [Fact]
    public async Task ResumedOnAnotherReplica_TickFiresWhereTheJobWasRemoved()
    {
        // Arrange — paused here earlier (Remove), resumed on another replica, which seeded a new chain.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        hub.Remove(JobId);
        lock (_scheduled) { _scheduled.Clear(); }
        var resumedChain = Tick(Cron, new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero));
        var consumer = CreateConsumer(hub, SharedState(paused: false));
        _clock.Now = resumedChain.ScheduledFor.AddMilliseconds(200);

        // Act
        await DeliverAsync(consumer, resumedChain);

        // Assert — the shared state says the job is live, so the resumed chain keeps running here too.
        Assert.Equal(1, _runner.TriggerCount);
        var next = Assert.Single(Scheduled());
        Assert.Equal(resumedChain.ChainEpoch, next.ChainEpoch);
    }

    [Fact]
    public async Task OneOffTick_FiresWithoutPerpetuating()
    {
        // Arrange — "Trigger now" on a registered job.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", Cron);
        lock (_scheduled) { _scheduled.Clear(); }
        var consumer = CreateConsumer(hub);
        var oneOff = Tick(string.Empty, _clock.Now, default);

        // Act
        await DeliverAsync(consumer, oneOff);

        // Assert
        Assert.Equal(1, _runner.TriggerCount);
        Assert.Empty(Scheduled());
    }

    [Fact]
    public async Task Consumer_DoesNotStartProcessingBeforeTheHostHasStarted()
    {
        // Arrange — FlowSyncHostedService registers the jobs after this service has started.
        var processorRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.CreateProcessor(Arg.Any<string>(), Arg.Any<ServiceBusProcessorOptions>())
            .Returns(_ =>
            {
                processorRequested.TrySetResult();
                return null!;
            });
        using var started = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(started.Token);
        var services = new ServiceCollection().AddSingleton<IServiceBusFlowRunner>(_runner).BuildServiceProvider();
        var sut = new ServiceBusCronProcessorHostedService(
            _client,
            new ServiceBusRuntimeOptions(),
            CreateHub(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ServiceBusCronProcessorHostedService>.Instance,
            _clock,
            lifetime);

        // Act
        await sut.StartAsync(CancellationToken.None);
        var requestedBeforeStart = processorRequested.Task.IsCompleted;
        await started.CancelAsync();
        await processorRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await sut.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(requestedBeforeStart, "The cron processor started before ApplicationStarted.");
    }
}
