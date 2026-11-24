using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Lifecycle of the self-perpetuating Service Bus cron chain when the schedule is changed or paused while a
/// tick is already sitting in the broker.
/// </summary>
/// <remarks>
/// <para>
/// The consumer (<see cref="ServiceBusCronProcessorHostedService"/>) computes the next tick from the cron
/// carried INSIDE the message it just received (<see cref="CronEnvelope.Cron"/>), and the hub's only guard
/// (<c>ScheduleNextAsync</c>) checks that the job id is still registered — not that the message belongs to the
/// job's current schedule. <c>RegisterOrUpdate</c> for an existing job schedules a brand-new first tick for
/// the new cron and never cancels the tick already queued for the old one.
/// </para>
/// <para>
/// Consequences: (1) changing a cron through the dashboard (<c>PUT /api/schedules/{jobId}/cron</c>) leaves the
/// old chain alive next to the new one — the flow fires on BOTH schedules for ever, across restarts, because
/// the job id stays registered; (2) a tick that is already in the broker when the job is paused (after a
/// restart the hub no longer knows that tick's sequence number, and on another replica the pause never
/// happened) still starts a run, because the consumer calls the engine whether or not the job is registered.
/// </para>
/// </remarks>
public sealed class ServiceBusCronChainLifecycleTests
{
    private const string OldCron = "*/5 * * * *";
    private const string NewCron = "0 * * * *";

    private readonly ServiceBusClient _client = Substitute.For<ServiceBusClient>();
    private readonly ServiceBusSender _sender = Substitute.For<ServiceBusSender>();
    private readonly ServiceBusReceiver _receiver = Substitute.For<ServiceBusReceiver>();
    private readonly RecordingRunner _runner = new();
    private readonly List<CronEnvelope> _scheduled = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 11, 1, 10, 0, 30, TimeSpan.Zero));
    private readonly Guid _flowId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private long _sequence;

    /// <summary>Routes every scheduled cron message to <see cref="_scheduled"/>.</summary>
    public ServiceBusCronChainLifecycleTests()
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
    }

    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Hand-rolled runner: the interface is internal, so Castle cannot proxy it.</summary>
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

    private string JobId => RecurringTriggerJobId.Create(_flowId, "tick");

    private ServiceBusRecurringTriggerHub CreateHub() =>
        new(_client, new ServiceBusRuntimeOptions(), NullLogger<ServiceBusRecurringTriggerHub>.Instance, _clock);

    private ServiceBusCronProcessorHostedService CreateConsumer(ServiceBusRecurringTriggerHub hub)
    {
        var services = new ServiceCollection().AddSingleton<IServiceBusFlowRunner>(_runner).BuildServiceProvider();
        return new ServiceBusCronProcessorHostedService(
            _client,
            new ServiceBusRuntimeOptions(),
            hub,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ServiceBusCronProcessorHostedService>.Instance,
            _clock);
    }

    /// <summary>Delivers <paramref name="envelope"/> to the consumer's private message handler.</summary>
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

    [Fact]
    public async Task CronChangedAtRuntime_TickFromTheOldChain_DoesNotKeepTheOldScheduleAlive()
    {
        // Arrange — the job runs every 5 minutes; its 10:05 tick is in the broker. The dashboard then
        // switches it to hourly.
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", OldCron);
        var oldChainTick = Assert.Single(_scheduled);
        hub.RegisterOrUpdate(JobId, _flowId, "tick", NewCron);
        lock (_scheduled) { _scheduled.Clear(); }
        var consumer = CreateConsumer(hub);
        _clock.Now = oldChainTick.ScheduledFor.AddMilliseconds(200);

        // Act — the broker delivers the old chain's 10:05 tick.
        await DeliverAsync(consumer, oldChainTick);

        // Assert — nothing may be scheduled on the replaced cron; otherwise the flow fires on both schedules for ever.
        List<CronEnvelope> perpetuated;
        lock (_scheduled) { perpetuated = _scheduled.Where(e => e.Cron == OldCron).ToList(); }
        Assert.True(perpetuated.Count == 0,
            $"Old cron '{OldCron}' re-scheduled itself for {string.Join(", ", perpetuated.Select(e => e.ScheduledFor.ToString("O")))} after the job was switched to '{NewCron}'.");
    }

    [Fact]
    public async Task PausedJob_TickAlreadyInTheBroker_DoesNotStartARun()
    {
        // Arrange — a restarted host re-registers the job, then an operator pauses it (RecurringTriggerSync /
        // the dashboard call Remove). The tick queued by the previous process is still in the broker: this
        // process never knew its sequence number, so Remove could not cancel it.
        var preRestartTick = new CronEnvelope
        {
            FlowId = _flowId,
            TriggerKey = "tick",
            Cron = OldCron,
            ScheduledFor = new DateTimeOffset(2026, 11, 1, 10, 5, 0, TimeSpan.Zero),
            JobId = JobId,
        };
        var hub = CreateHub();
        hub.RegisterOrUpdate(JobId, _flowId, "tick", OldCron);
        hub.Remove(JobId);
        var consumer = CreateConsumer(hub);
        _clock.Now = preRestartTick.ScheduledFor.AddMilliseconds(200);

        // Act
        await DeliverAsync(consumer, preRestartTick);

        // Assert — a paused schedule must not start a run.
        Assert.True(_runner.TriggerCount == 0,
            $"The paused job's in-flight tick still started {_runner.TriggerCount} run(s).");
    }
}
