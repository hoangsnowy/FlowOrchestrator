namespace FlowOrchestrator.Core.Storage;

public sealed class FlowDefinitionRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Version { get; set; } = "1.0";
    public string? ManifestJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
