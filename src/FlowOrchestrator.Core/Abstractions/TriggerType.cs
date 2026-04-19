namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Supported trigger types for flows.
/// Only <see cref="Webhook"/> has an external URL for integration from outside systems.
/// </summary>
public enum TriggerType
{
    /// <summary>Started on demand via the dashboard or the trigger API.</summary>
    Manual,

    /// <summary>Started by an inbound HTTP POST to the flow's webhook endpoint.</summary>
    Webhook,

    /// <summary>Started automatically on a cron schedule.</summary>
    Cron
}
