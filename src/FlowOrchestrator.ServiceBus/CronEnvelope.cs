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

    /// <summary>
    /// Identifies the self-perpetuating chain this tick belongs to: the instant of the chain's first tick,
    /// raised above every earlier chain of the same job. Every tick a chain perpetuates carries it unchanged.
    /// </summary>
    /// <remarks>
    /// The consumer fires only the newest chain of a job and drops ticks of an older one, which is how a cron
    /// change, a restart or a resume replaces a chain without leaving the old one running next to it.
    /// <see langword="default"/> on a one-off fire and on ticks written before 2.0 (they rank below any chain
    /// seeded since).
    /// </remarks>
    [JsonPropertyName("chainEpoch")]
    public DateTimeOffset ChainEpoch { get; set; }
}
