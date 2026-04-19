namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persisted representation of a flow definition stored in the database or in-memory store.
/// The <see cref="ManifestJson"/> column holds the serialised <see cref="FlowOrchestrator.Core.Abstractions.FlowManifest"/>.
/// </summary>
public sealed class FlowDefinitionRecord
{
    /// <summary>Stable unique identifier matching <see cref="FlowOrchestrator.Core.Abstractions.IFlowDefinition.Id"/>.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable display name shown in the dashboard.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Semver-style version label matching <see cref="FlowOrchestrator.Core.Abstractions.IFlowDefinition.Version"/>.</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// JSON-serialised <see cref="FlowOrchestrator.Core.Abstractions.FlowManifest"/>.
    /// <see langword="null"/> for flows defined entirely in code without a JSON override.
    /// </summary>
    public string? ManifestJson { get; set; }

    /// <summary>
    /// When <see langword="false"/>, the scheduler skips this flow and the dashboard hides it
    /// from the active flow list.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Timestamp when the record was first inserted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Timestamp of the last upsert.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
