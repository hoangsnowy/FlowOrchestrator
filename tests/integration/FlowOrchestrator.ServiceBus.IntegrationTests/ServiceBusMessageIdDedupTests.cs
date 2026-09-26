using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.ServiceBus;
using NSubstitute;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// Broker-level proof for #186 against a real (emulated) entity with duplicate detection ON, the way
/// production creates the step topic: two dispatches of the same step with the same
/// <c>ScheduledTime</c> must both arrive.
/// </summary>
/// <remarks>
/// The control case sends one message twice with an identical <c>MessageId</c> and expects one
/// delivery, which is what makes the main assertion meaningful: it shows the entity really does
/// drop duplicates, so the step ids are distinct by construction and not just lucky.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class ServiceBusMessageIdDedupTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly ServiceBusEmulatorFixture _fixture;

    /// <summary>Binds the shared emulator.</summary>
    public ServiceBusMessageIdDedupTests(ServiceBusEmulatorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_dispatches_of_one_step_with_a_pinned_ScheduledTime_both_arrive()
    {
        // Arrange — the exact #186 collision: same run, same step, same ScheduledTime tick.
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender(ServiceBusEmulatorFixture.DedupProbeQueueName);
        await using var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.DedupProbeQueueName);
        await DrainAsync(receiver);
        var ctx = new Core.Execution.ExecutionContext { RunId = Guid.NewGuid() };
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(Guid.NewGuid());
        var pinned = new DateTimeOffset(2026, 9, 8, 13, 43, 13, TimeSpan.Zero);
        var first = ServiceBusStepDispatcher.BuildMessage(ctx, flow, new StepInstance("check_shipment_status", "Poll") { RunId = ctx.RunId, ScheduledTime = pinned }, null);
        var second = ServiceBusStepDispatcher.BuildMessage(ctx, flow, new StepInstance("check_shipment_status", "Poll") { RunId = ctx.RunId, ScheduledTime = pinned }, null);

        // Act
        await sender.SendMessageAsync(first);
        await sender.SendMessageAsync(second);
        var received = await ReceiveAsync(receiver, expected: 2);

        // Assert
        Assert.Equal(2, received.Count);
        Assert.Equal(
            new[] { first.MessageId, second.MessageId }.Order(),
            received.Select(m => m.MessageId).Order());
    }

    [Fact]
    public async Task Control_the_probe_entity_really_drops_a_repeated_MessageId()
    {
        // Arrange
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender(ServiceBusEmulatorFixture.DedupProbeQueueName);
        await using var receiver = client.CreateReceiver(ServiceBusEmulatorFixture.DedupProbeQueueName);
        await DrainAsync(receiver);
        var id = $"{Guid.NewGuid()}:check_shipment_status:638613765930000000"; // the pre-#186 shape

        // Act
        await sender.SendMessageAsync(new ServiceBusMessage("a") { MessageId = id });
        await sender.SendMessageAsync(new ServiceBusMessage("b") { MessageId = id });
        var received = await ReceiveAsync(receiver, expected: 2);

        // Assert — one delivery: this is the silent drop #186 was about.
        var only = Assert.Single(received);
        Assert.Equal(id, only.MessageId);
    }

    /// <summary>Receives until <paramref name="expected"/> messages arrived or the stream goes quiet.</summary>
    private static async Task<List<ServiceBusReceivedMessage>> ReceiveAsync(ServiceBusReceiver receiver, int expected)
    {
        var received = new List<ServiceBusReceivedMessage>();
        using var budget = new CancellationTokenSource(Budget);
        while (received.Count < expected && !budget.IsCancellationRequested)
        {
            // Settle, not a deadline assertion: once the first message is in, give a second one a fair
            // window to show up before concluding it was dropped.
            var wait = received.Count == 0 ? Budget : Settle;
            var batch = await receiver.ReceiveMessagesAsync(expected - received.Count, wait, budget.Token);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var message in batch)
            {
                await receiver.CompleteMessageAsync(message, budget.Token);
                received.Add(message);
            }
        }

        return received;
    }

    /// <summary>Clears anything a previous test left on the shared probe queue.</summary>
    private static async Task DrainAsync(ServiceBusReceiver receiver)
    {
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(50, TimeSpan.FromMilliseconds(500));
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var message in batch)
            {
                await receiver.CompleteMessageAsync(message);
            }
        }
    }
}
