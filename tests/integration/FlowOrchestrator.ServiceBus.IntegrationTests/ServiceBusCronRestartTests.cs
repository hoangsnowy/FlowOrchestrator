using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// A restarted host must not add a second tick next to the one the previous process left on the cron queue.
/// </summary>
/// <remarks>
/// The emulator's cron queue runs WITHOUT duplicate detection, which is exactly the position production is
/// in once the surviving tick was sent more than the 10-minute window ago: a second tick under the same
/// message id is accepted, and the flow fires twice at that instant. The hub must peek the queue at startup
/// and adopt the surviving tick.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
[Trait("Category", "ServiceBusEmulator")]
public sealed class ServiceBusCronRestartTests
{
    private const string TriggerKey = "yearly";
    private const string QueueName = "flow-cron-triggers";

    private readonly ServiceBusEmulatorFixture _fixture;

    /// <summary>Captures the fixture.</summary>
    public ServiceBusCronRestartTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    private static string JobId => RecurringTriggerJobId.Create(ServiceBusEmulatorFixture.CronTestFlowId, TriggerKey);

    private IHost BuildHost() => Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddFlowOrchestrator(opts =>
            {
                opts.UseInMemory();
                opts.UseAzureServiceBusRuntime(sb =>
                {
                    sb.ConnectionString = _fixture.ConnectionString;
                    sb.AutoCreateTopology = false; // emulator topology is static
                });
                opts.AddFlow<YearlyCronFlow>();
            });
            services.AddStepHandler<CronTickHandler>("CronTickStep");
        })
        .Build();

    /// <summary>Starts a host and waits until its startup registrations have been scheduled or adopted.</summary>
    private static async Task<IHost> StartAsync(IHost host)
    {
        await host.StartAsync();
        await host.Services.GetRequiredService<ServiceBusRecurringTriggerHub>().CompleteStartupAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return host;
    }

    private async Task<List<(long Sequence, CronEnvelope Tick)>> PeekTicksAsync()
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var receiver = client.CreateReceiver(QueueName);
        var ticks = new List<(long, CronEnvelope)>();
        long? from = null;
        while (true)
        {
            var page = await receiver.PeekMessagesAsync(250, from);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var message in page)
            {
                var tick = JsonSerializer.Deserialize<CronEnvelope>(message.Body.ToArray());
                if (tick is not null && tick.JobId == JobId && !string.IsNullOrEmpty(tick.Cron)
                    && message.State == ServiceBusMessageState.Scheduled)
                {
                    ticks.Add((message.SequenceNumber, tick));
                }
            }

            from = page[^1].SequenceNumber + 1;
        }

        return ticks;
    }

    private async Task CancelAsync(IEnumerable<long> sequences)
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender(QueueName);
        foreach (var sequence in sequences)
        {
            try { await sender.CancelScheduledMessageAsync(sequence); }
            catch (ServiceBusException) { /* already gone */ }
        }
    }

    [Fact]
    public async Task Restart_TickLeftByThePreviousProcess_IsAdoptedNotDuplicated()
    {
        // Arrange — process 1 seeds the yearly tick, then stops; the tick stays scheduled on the queue.
        await CancelAsync((await PeekTicksAsync()).Select(t => t.Sequence));
        using (var first = await StartAsync(BuildHost()))
        {
            await first.StopAsync(TimeSpan.FromSeconds(10));
        }

        var afterFirst = await PeekTicksAsync();
        Assert.Single(afterFirst);

        // Act — process 2 starts on the same queue.
        List<(long Sequence, CronEnvelope Tick)> afterRestart;
        using (var second = await StartAsync(BuildHost()))
        {
            afterRestart = await PeekTicksAsync();
            await second.StopAsync(TimeSpan.FromSeconds(10));
        }

        // Assert — still exactly one tick for that instant: the surviving one.
        try
        {
            var tick = Assert.Single(afterRestart);
            Assert.Equal(afterFirst[0].Sequence, tick.Sequence);
        }
        finally
        {
            await CancelAsync(afterRestart.Select(t => t.Sequence));
        }
    }
}

/// <summary>Cron flow that fires once a year, so no tick is delivered while the test runs.</summary>
internal sealed class YearlyCronFlow : IFlowDefinition
{
    /// <inheritdoc/>
    public Guid Id { get; } = ServiceBusEmulatorFixture.CronTestFlowId;

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <inheritdoc/>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["yearly"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 0 1 1 *" },
            },
        },
        Steps = new StepCollection
        {
            ["only_step"] = new StepMetadata
            {
                Type = "CronTickStep",
                Inputs = new Dictionary<string, object?> { ["payload"] = "tick" },
            },
        },
    };
}
