using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Hangfire;
using FlowOrchestrator.InMemory;
using FlowOrchestrator.ServiceBus;
using global::Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowOrchestrator.ServiceBus.IntegrationTests;

/// <summary>
/// End-to-end smoke test for the Service Bus cron path. Verifies the full chain:
/// <list type="number">
///   <item><c>FlowSyncHostedService</c> calls <c>SyncTriggers(flowId, true)</c> on startup.</item>
///   <item><c>ServiceBusRecurringTriggerHub.RegisterOrUpdate</c> enqueues the FIRST scheduled message.</item>
///   <item>Service Bus delivers the message at the scheduled fire time.</item>
///   <item><c>ServiceBusCronProcessorHostedService.OnMessageAsync</c> drains it.</item>
///   <item>The consumer self-perpetuates the NEXT fire BEFORE invoking the engine.</item>
///   <item><c>TriggerByScheduleAsync</c> dispatches the flow's only step through the topic.</item>
///   <item>The step subscription processor picks up the step, the engine runs the handler.</item>
///   <item>The handler counter increments. Repeat for ticks 2, 3, … to prove self-perpetuation.</item>
/// </list>
/// </summary>
/// <remarks>
/// Uses a 6-field every-second cron (<c>* * * * * *</c>) so we observe ≥3 fires within a 30 s budget.
/// The test asserts at least 3 distinct ticks so an accidentally-deleted self-perpetuation step
/// (e.g. a bad early-return inside <c>ScheduleNextAsync</c>) would be caught — a single fire is not
/// enough to prove cron "works" end-to-end because <c>RegisterOrUpdate</c> alone enqueues the first.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
[Trait("Category", "ServiceBusEmulator")]
public class ServiceBusCronRoundTripTests : IAsyncLifetime
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private IHost? _host;

    /// <summary>Captures the fixture for use during the test lifecycle.</summary>
    public ServiceBusCronRoundTripTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        CronTickHandler.Reset();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHangfire(c => c.UseInMemoryStorage());
                services.AddFlowOrchestrator(opts =>
                {
                    opts.UseInMemory();
                    opts.UseAzureServiceBusRuntime(sb =>
                    {
                        sb.ConnectionString = _fixture.ConnectionString;
                        sb.AutoCreateTopology = false; // emulator topology is static
                    });
                    opts.AddFlow<EverySecondCronFlow>();
                });
                services.AddStepHandler<CronTickHandler>("CronTickStep");
            })
            .Build();

        await _host.StartAsync();
        // Give the SB processors a moment to register their message-pumps before
        // the first cron message is delivered.
        await Task.Delay(500);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
    }

    /// <summary>
    /// The smoking-gun test for "Cron khong duoc trong Service Bus". If this passes, the cron
    /// path is wired correctly end-to-end. If it fails, the diagnostic is the tick count: 0
    /// means RegisterOrUpdate never enqueued; 1 means self-perpetuation is broken.
    /// </summary>
    [Fact]
    public async Task EverySecondCron_FiresMultipleTicks_ThroughEmulator()
    {
        // Arrange
        Assert.NotNull(_host);
        var inspector = _host!.Services.GetRequiredService<IRecurringTriggerInspector>();

        // Sanity: the hub knows about the cron job (proves SyncTriggers ran).
        var jobs = await inspector.GetJobsAsync();
        Assert.Contains(jobs, j => j.Cron == "* * * * * *");

        // Act — wait until at least 3 fires accrue, with a generous 30 s budget for CI contention.
        var thirdTick = CronTickHandler.WaitForCountAsync(3);
        var completed = await Task.WhenAny(thirdTick, Task.Delay(TimeSpan.FromSeconds(30)));

        // Assert
        Assert.True(
            completed == thirdTick,
            $"Cron failed to self-perpetuate: only saw {CronTickHandler.InvocationCount} fires in 30 s. " +
            $"Expected at least 3.");
        Assert.True(CronTickHandler.InvocationCount >= 3,
            $"Tick counter regressed to {CronTickHandler.InvocationCount}.");
    }
}

/// <summary>
/// Cron flow that fires every second through the Service Bus runtime. Uses the
/// dedicated cron flow id so the emulator's pre-provisioned subscription matches.
/// </summary>
internal sealed class EverySecondCronFlow : IFlowDefinition
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
            ["schedule"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?>
                {
                    // 6-field cron: second-precision "every second".
                    ["cronExpression"] = "* * * * * *",
                },
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

/// <summary>
/// Step handler that increments a counter on every invocation and exposes a
/// <see cref="WaitForCountAsync"/> primitive so tests can wait on a logical event
/// instead of polling on wall-clock — adheres to the project's anti-flake rules.
/// </summary>
internal sealed class CronTickHandler : IStepHandler
{
    private static int _count;
    private static readonly object _gate = new();
    private static readonly List<(int target, TaskCompletionSource<int> tcs)> _waiters = new();

    /// <summary>Number of times the handler has been invoked since the last <see cref="Reset"/>.</summary>
    public static int InvocationCount => Volatile.Read(ref _count);

    /// <summary>Resets the counter and clears any pending waiters. Call from test setup.</summary>
    public static void Reset()
    {
        Volatile.Write(ref _count, 0);
        lock (_gate)
        {
            foreach (var (_, tcs) in _waiters) tcs.TrySetCanceled();
            _waiters.Clear();
        }
    }

    /// <summary>Returns a task that completes when <see cref="InvocationCount"/> reaches <paramref name="target"/>.</summary>
    /// <param name="target">The minimum invocation count to wait for.</param>
    public static Task<int> WaitForCountAsync(int target)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (Volatile.Read(ref _count) >= target)
            {
                tcs.TrySetResult(_count);
            }
            else
            {
                _waiters.Add((target, tcs));
            }
        }
        return tcs.Task;
    }

    /// <inheritdoc/>
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        var n = Interlocked.Increment(ref _count);
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (n >= _waiters[i].target)
                {
                    _waiters[i].tcs.TrySetResult(n);
                    _waiters.RemoveAt(i);
                }
            }
        }
        return new ValueTask<object?>(new { ok = true, n });
    }
}
