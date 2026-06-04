using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Configuration;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOrchestrator.ServiceBus.UnitTests;

/// <summary>
/// Regression coverage for the "Trigger now forks the schedule" bug: <see cref="ServiceBusRecurringTriggerHub.TriggerOnce"/>
/// must enqueue a one-off cron message with an EMPTY <c>Cron</c>. The cron consumer
/// (<see cref="ServiceBusCronProcessorHostedService"/>) only self-perpetuates the next firing when the
/// message's <c>Cron</c> is non-empty, so a non-empty cron here would spawn a second recurring chain on
/// top of the existing one — firing an active (non-paused) job twice forever.
/// </summary>
/// <remarks>
/// The SDK's <see cref="ServiceBusSender"/> is sealed and the hub realises a real sender lazily, so we
/// cannot intercept the wire send. Instead we assert on the message the hub WOULD enqueue via the
/// <see cref="ServiceBusRecurringTriggerHub.TryBuildTriggerOnceMessage"/> seam, decoding the serialised
/// <c>CronEnvelope</c> body. This is the exact payload the consumer reads to decide on perpetuation.
/// </remarks>
public class ServiceBusTriggerOnceTests
{
    private const string FakeConnString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAAA";

    [Fact]
    public void TriggerOnce_ActiveRecurringJob_EnqueuesMessageWithEmptyCron()
    {
        // Arrange — a registered recurring job with a non-empty cron (the case that used to fork).
        var hub = CreateBareHub();
        var flowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string triggerKey = "schedule";
        const string cron = "*/5 * * * * *";
        var jobId = $"flow-{flowId}-{triggerKey}";
        SeedJob(hub, jobId, flowId, triggerKey, cron);

        // Act — build the one-off message exactly as TriggerOnce would enqueue it.
        var found = hub.TryBuildTriggerOnceMessage(jobId, out var resolvedFlowId, out var resolvedTriggerKey, out var message);

        // Assert — one-off semantics: empty cron so the consumer does NOT perpetuate a second chain.
        Assert.True(found);
        Assert.Equal(flowId, resolvedFlowId);
        Assert.Equal(triggerKey, resolvedTriggerKey);
        Assert.NotNull(message);
        var envelope = DecodeEnvelope(message!);
        Assert.Equal(string.Empty, envelope.Cron);
        Assert.Equal(flowId, envelope.FlowId);
        Assert.Equal(triggerKey, envelope.TriggerKey);
        Assert.Equal(jobId, envelope.JobId);
    }

    [Fact]
    public void TriggerOnce_UnknownJob_ProducesNoMessage()
    {
        // Arrange — hub with no jobs registered.
        var hub = CreateBareHub();

        // Act
        var found = hub.TryBuildTriggerOnceMessage("flow-missing-job", out _, out _, out var message);

        // Assert — unknown job is a no-op; nothing is enqueued.
        Assert.False(found);
        Assert.Null(message);
    }

    /// <summary>
    /// Sanity check on the consumer-side contract this fix relies on: an empty cron means the message
    /// envelope's <c>Cron</c> is empty, which the cron processor treats as "fire once, do not perpetuate".
    /// </summary>
    [Fact]
    public void BuildCronMessage_EmptyCron_SerialisesEmptyCronEnvelope()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);

        // Act
        var message = ServiceBusRecurringTriggerHub.BuildCronMessage(
            jobId: "flow-x-schedule",
            flowId: flowId,
            triggerKey: "schedule",
            cron: string.Empty,
            fireAt: now,
            now: now);

        // Assert
        var envelope = DecodeEnvelope(message);
        Assert.Equal(string.Empty, envelope.Cron);
    }

    private static DecodedEnvelope DecodeEnvelope(ServiceBusMessage message)
    {
        var json = message.Body.ToArray();
        return JsonSerializer.Deserialize<DecodedEnvelope>(json)
            ?? throw new InvalidOperationException("Failed to decode cron envelope.");
    }

    /// <summary>
    /// Mirror of the internal <c>CronEnvelope</c> wire format so the test can decode the message body
    /// without reaching into the internal type. Property names match the <c>[JsonPropertyName]</c> attrs.
    /// </summary>
    private sealed record DecodedEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("flowId")]
        public Guid FlowId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("triggerKey")]
        public string TriggerKey { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("cron")]
        public string Cron { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("jobId")]
        public string JobId { get; init; } = string.Empty;
    }

    private static ServiceBusRecurringTriggerHub CreateBareHub() =>
        new(
            new ServiceBusClient(FakeConnString),
            new ServiceBusRuntimeOptions { ConnectionString = FakeConnString },
            new EmptyFlowRepository(),
            new NoopScheduleStateStore(),
            new FlowSchedulerOptions { PersistOverrides = false },
            NullLogger<ServiceBusRecurringTriggerHub>.Instance);

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

        var tryAdd = jobs.GetType().GetMethod("TryAdd")!;
        tryAdd.Invoke(jobs, [jobId, state]);
    }

    private static void SetField(Type t, object instance, string fieldName, object value)
    {
        var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{fieldName} field not found on {t}");
        f.SetValue(instance, value);
    }

    private sealed class EmptyFlowRepository : IFlowRepository
    {
        public ValueTask<IReadOnlyList<IFlowDefinition>> GetAllFlowsAsync() =>
            new(Array.Empty<IFlowDefinition>());
    }

    private sealed class NoopScheduleStateStore : IFlowScheduleStateStore
    {
        public Task<FlowScheduleState?> GetAsync(string jobId) => Task.FromResult<FlowScheduleState?>(null);
        public Task<IReadOnlyList<FlowScheduleState>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<FlowScheduleState>>(Array.Empty<FlowScheduleState>());
        public Task SaveAsync(FlowScheduleState state) => Task.CompletedTask;
        public Task DeleteAsync(string jobId) => Task.CompletedTask;
    }
}
