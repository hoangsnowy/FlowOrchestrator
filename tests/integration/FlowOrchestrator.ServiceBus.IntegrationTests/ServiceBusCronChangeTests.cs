using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// Changing a running cron schedule through the emulator, on a cron queue WITH duplicate detection (as
/// production provisions it).
/// </summary>
/// <remarks>
/// The new schedule <c>0-59 * * * * *</c> fires on exactly the same seconds as the running <c>* * * * * *</c>,
/// so the new chain's first tick always lands on the instant of the old chain's already-scheduled tick. Before
/// the fix both ticks carried the same message id, the broker discarded the new chain's first tick as a
/// duplicate, the old tick was cancelled, and the schedule never fired again.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
[Trait("Category", "ServiceBusEmulator")]
public sealed class ServiceBusCronChangeTests
{
    private const string TriggerKey = "change";
    private const string OriginalCron = "* * * * * *";
    private const string ChangedCron = "0-59 * * * * *";

    private readonly ServiceBusEmulatorFixture _fixture;

    /// <summary>Captures the fixture.</summary>
    public ServiceBusCronChangeTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CronChangedWhileTheNextTickIsScheduled_NewScheduleKeepsFiring()
    {
        // Arrange — a running every-second chain on the deduplicating cron queue.
        CronChangeTickHandler.Reset();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFlowOrchestrator(opts =>
                {
                    opts.UseInMemory();
                    opts.UseAzureServiceBusRuntime(sb =>
                    {
                        sb.ConnectionString = _fixture.ConnectionString;
                        sb.AutoCreateTopology = false; // emulator topology is static
                        sb.CronQueueName = ServiceBusEmulatorFixture.DedupCronQueueName;
                    });
                    opts.AddFlow<CronChangeFlow>();
                });
                services.AddStepHandler<CronChangeTickHandler>("CronChangeStep");
            })
            .Build();
        await host.StartAsync();
        try
        {
            await CronChangeTickHandler.WaitForCountAsync(3).WaitAsync(TimeSpan.FromSeconds(30));

            // Act — what the dashboard's PUT /api/schedules/{jobId}/cron does: schedule, then persist.
            var jobId = RecurringTriggerJobId.Create(ServiceBusEmulatorFixture.CronTestFlowId, TriggerKey);
            host.Services.GetRequiredService<IRecurringTriggerDispatcher>()
                .RegisterOrUpdate(jobId, ServiceBusEmulatorFixture.CronTestFlowId, TriggerKey, ChangedCron);
            await host.Services.GetRequiredService<IFlowScheduleStateStore>().SaveAsync(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = ServiceBusEmulatorFixture.CronTestFlowId,
                FlowName = nameof(CronChangeFlow),
                TriggerKey = TriggerKey,
                CronOverride = ChangedCron,
            });
            var afterChange = CronChangeTickHandler.InvocationCount;

            // Assert — the changed schedule keeps firing (a 30 s budget for three one-second ticks).
            var keptFiring = CronChangeTickHandler.WaitForCountAsync(afterChange + 3);
            var completed = await Task.WhenAny(keptFiring, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(completed == keptFiring,
                $"The schedule stopped after the cron change: {CronChangeTickHandler.InvocationCount - afterChange} run(s) in 30 s.");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>Every-second cron flow whose schedule the test changes at runtime.</summary>
internal sealed class CronChangeFlow : IFlowDefinition
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
            ["change"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "* * * * * *" },
            },
        },
        Steps = new StepCollection
        {
            ["change_step"] = new StepMetadata { Type = "CronChangeStep" },
        },
    };
}

/// <summary>Counts runs and lets the test wait on a count instead of polling the clock.</summary>
internal sealed class CronChangeTickHandler : IStepHandler
{
    private static int _count;
    private static readonly Lock Gate = new();
    private static readonly List<(int Target, TaskCompletionSource Tcs)> Waiters = new();

    /// <summary>Runs since the last <see cref="Reset"/>.</summary>
    public static int InvocationCount => Volatile.Read(ref _count);

    /// <summary>Clears the counter and pending waiters.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Volatile.Write(ref _count, 0);
            Waiters.Clear();
        }
    }

    /// <summary>Completes once <see cref="InvocationCount"/> reaches <paramref name="target"/>.</summary>
    /// <param name="target">The count to wait for.</param>
    /// <returns>A task completing when the count is reached.</returns>
    public static Task WaitForCountAsync(int target)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (Volatile.Read(ref _count) >= target)
            {
                tcs.TrySetResult();
            }
            else
            {
                Waiters.Add((target, tcs));
            }
        }

        return tcs.Task;
    }

    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        lock (Gate)
        {
            var count = Interlocked.Increment(ref _count);
            foreach (var (target, tcs) in Waiters)
            {
                if (count >= target)
                {
                    tcs.TrySetResult();
                }
            }
        }

        return ValueTask.FromResult<object?>(null);
    }
}
