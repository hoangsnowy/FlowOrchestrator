namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Named collection of <see cref="TriggerMetadata"/> entries for a flow manifest,
/// keyed by the trigger's logical name (e.g. <c>"manual"</c>, <c>"schedule"</c>, <c>"webhook"</c>).
/// </summary>
public sealed class FlowTriggerCollection : Dictionary<string, TriggerMetadata>
{
}
