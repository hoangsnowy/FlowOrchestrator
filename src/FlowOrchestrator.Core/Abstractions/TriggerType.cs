namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Supported trigger types for flows.
/// Only <see cref="Webhook"/> has an external URL for integration from outside systems.
/// </summary>
public enum TriggerType
{
    Manual,
    Webhook,
    Cron
}
