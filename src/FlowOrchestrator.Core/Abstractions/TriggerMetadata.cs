namespace FlowOrchestrator.Core.Abstractions;

public sealed class TriggerMetadata
{
    public string Type { get; set; } = default!;
    public IDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();
}
