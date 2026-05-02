using System.Text.Json.Serialization;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Wire-format DTO for a self-perpetuating cron message on the cron queue.
/// </summary>
internal sealed class CronEnvelope
{
    /// <summary>The flow whose cron trigger should fire.</summary>
    [JsonPropertyName("flowId")]
    public Guid FlowId { get; set; }

    /// <summary>Manifest trigger key (e.g. <c>"schedule"</c>).</summary>
    [JsonPropertyName("triggerKey")]
    public string TriggerKey { get; set; } = string.Empty;

    /// <summary>Cron expression effective at the time the message was enqueued.</summary>
    [JsonPropertyName("cron")]
    public string Cron { get; set; } = string.Empty;

    /// <summary>UTC instant the message is scheduled to fire.</summary>
    [JsonPropertyName("scheduledFor")]
    public DateTimeOffset ScheduledFor { get; set; }

    /// <summary>Stable scheduler job identifier (e.g. <c>"flow-{id}-{triggerKey}"</c>).</summary>
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = string.Empty;
}
