using System.Reflection;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Regression coverage for invariant #21 / known-issue #5 (qa-agent.md, section J):
/// when a flow is disabled, the cron consumer's call to
/// <c>ServiceBusRecurringTriggerHub.ScheduleNextAsync</c> must NOT enqueue a new
/// scheduled message, otherwise the engine's IsEnabled gate (#18) keeps rejecting
/// fires forever while a fresh tick is enqueued every cycle.
/// </summary>
/// <remarks>
/// We can't drive the full flow — the SDK's <c>ServiceBusSender</c> is sealed and the
/// hub uses a real client lazily. The test instead verifies the seam: when
/// <c>RecurringTriggerSync.SyncTriggersAsync(flowId, false)</c> calls <c>Remove(jobId)</c>, the hub's
/// <c>_jobs</c> dictionary no longer contains the entry, and
/// <c>ScheduleNextAsync</c> short-circuits before <c>_sender.Value</c> is ever
/// realised. Sender realisation is observed via the <c>Lazy&lt;ServiceBusSender&gt;</c>'s
/// <c>IsValueCreated</c> flag — the cleanest proof that no enqueue was attempted.
/// </remarks>
public class ServiceBusCronDisabledFlowTests
{
    private const string FakeConnString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    [Fact]
    public async Task ScheduleNextAsync_RemovedJob_DoesNotTouchSender()
    {
        // Arrange — register a job, then remove it the way SyncTriggersAsync(disabled) would.
        var (hub, _, _) = CreateHubWithRegisteredJob(out var jobId, out var flowId, out var triggerKey, out var cron);
        hub.Remove(jobId); // simulates SyncTriggersAsync(flowId, isEnabled: false) → Remove(jobId)

        // Act — cron consumer calls ScheduleNextAsync regardless of disable state.
        await hub.ScheduleNextAsync(
            jobId, flowId, triggerKey, cron,
            fireAt: DateTimeOffset.UtcNow.AddMinutes(1),
            ct: CancellationToken.None);

        // Assert — sender was never realised, proving no enqueue attempt.
        Assert.False(IsSenderRealised(hub));
    }

    [Fact]
    public async Task ScheduleNextAsync_UnknownJob_DoesNotTouchSender()
    {
        // Arrange — hub has NO jobs registered at all (never seen this jobId).
        var hub = CreateBareHub();

        // Act — cron consumer might see a stale message after a hub restart that lost in-memory state.
        await hub.ScheduleNextAsync(
            jobId: "flow-stale-job",
            flowId: Guid.NewGuid(),
            triggerKey: "schedule",
            cron: "* * * * *",
            fireAt: DateTimeOffset.UtcNow.AddMinutes(1),
            ct: CancellationToken.None);

        // Assert
        Assert.False(IsSenderRealised(hub));
    }

    [Fact]
    public async Task SyncTriggersAsync_DisabledFlow_RemovesJobFromMap()
    {
        // Arrange — Core's single effective-cron sync drives the Service Bus hub as its dispatcher.
        var (hub, flow, registry) = CreateHubWithRegisteredJob(out var jobId, out _, out _, out _);
        var sync = new RecurringTriggerSync(
            registry,
            hub,
            new InMemoryFlowScheduleStateStore(),
            new FlowSchedulerOptions { PersistOverrides = false });

        // Act — flip flow to disabled.
        await sync.SyncTriggersAsync(flow.Id, isEnabled: false);

        // Assert — job is gone from the map, so the next cron fire's ScheduleNextAsync short-circuits.
        Assert.False(hub.TryGetJob(jobId, out _));
    }

    /// <summary>
    /// Reflects into the hub's private <c>Lazy&lt;ServiceBusSender&gt;</c> field to
    /// observe whether the sender has ever been touched. <c>IsValueCreated</c> stays
    /// <see langword="false"/> until <c>_sender.Value</c> is read for the first time.
    /// </summary>
    private static bool IsSenderRealised(ServiceBusRecurringTriggerHub hub)
    {
        var field = typeof(ServiceBusRecurringTriggerHub).GetField("_sender", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("hub _sender field not found");
        var lazy = (Lazy<ServiceBusSender>)field.GetValue(hub)!;
        return lazy.IsValueCreated;
    }

    private static ServiceBusRecurringTriggerHub CreateBareHub()
    {
        var client = new ServiceBusClient(FakeConnString);
        var options = new ServiceBusRuntimeOptions { ConnectionString = FakeConnString };
        return new ServiceBusRecurringTriggerHub(
            client,
            options,
            NullLogger<ServiceBusRecurringTriggerHub>.Instance);
    }

    private static (ServiceBusRecurringTriggerHub hub, IFlowDefinition flow, FlowRegistry registry)
        CreateHubWithRegisteredJob(out string jobId, out Guid flowId, out string triggerKey, out string cron)
    {
        flowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        triggerKey = "schedule";
        cron = "* * * * *";
        jobId = RecurringTriggerJobId.Create(flowId, triggerKey);

        var flow = new StubFlowDefinition(flowId, triggerKey, cron);
        var registry = new FlowRegistry([flow]);
        var hub = new ServiceBusRecurringTriggerHub(
            new ServiceBusClient(FakeConnString),
            new ServiceBusRuntimeOptions { ConnectionString = FakeConnString },
            NullLogger<ServiceBusRecurringTriggerHub>.Instance);

        // Manually populate _jobs without firing the fire-and-forget ScheduleNextAsync from
        // RegisterOrUpdate (which would touch the sender). We do this by reflecting a JobState
        // into the dictionary using the public TryGetJob path — easier, just use SyncTriggersAsync
        // with isEnabled: true, but RegisterOrUpdate itself fires a background ScheduleNextAsync
        // that races. To stay deterministic we use reflection.
        SeedJob(hub, jobId, flowId, triggerKey, cron);
        return (hub, flow, registry);
    }

    private static void SeedJob(ServiceBusRecurringTriggerHub hub, string jobId, Guid flowId, string triggerKey, string cron)
    {
        var jobsField = typeof(ServiceBusRecurringTriggerHub).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_jobs field not found");
        var jobs = jobsField.GetValue(hub)!;
        var jobStateType = typeof(ServiceBusRecurringTriggerHub).GetNestedType("JobState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("JobState nested type not found");
        var state = Activator.CreateInstance(jobStateType, nonPublic: true)!;
        SetField(jobStateType, state, "FlowId", flowId);
        SetField(jobStateType, state, "TriggerKey", triggerKey);
        SetField(jobStateType, state, "EffectiveCron", cron);
        SetField(jobStateType, state, "NextExecution", DateTimeOffset.UtcNow.AddMinutes(1));

        // jobs is ConcurrentDictionary<string, JobState> — call its TryAdd via reflection.
        var tryAdd = jobs.GetType().GetMethod("TryAdd")!;
        tryAdd.Invoke(jobs, [jobId, state]);
    }

    private static void SetField(Type t, object instance, string fieldName, object value)
    {
        var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{fieldName} field not found on {t}");
        f.SetValue(instance, value);
    }

    /// <summary>
    /// Hand-rolled flow definition stub. NSubstitute confuses async return types with
    /// Manifest property chains, so we write the stub directly.
    /// </summary>
    private sealed class StubFlowDefinition : IFlowDefinition
    {
        public StubFlowDefinition(Guid id, string triggerKey, string cron)
        {
            Id = id;
            Manifest = new FlowManifest();
            Manifest.Triggers[triggerKey] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = cron },
            };
        }

        public Guid Id { get; }
        public string Version => "1.0";
        public FlowManifest Manifest { get; set; }
    }
}
