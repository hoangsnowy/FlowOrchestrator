namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Represents a flow definition registered with the orchestrator.
/// Implement this interface to declare a flow in code; the orchestrator reads
/// <see cref="Manifest"/> to build the execution graph at runtime.
/// </summary>
public interface IFlowDefinition
{
    /// <summary>Stable unique identifier for this flow across deployments.</summary>
    Guid Id { get; }

    /// <summary>Semver-style version label used for display and audit purposes.</summary>
    string Version { get; }

    /// <summary>
    /// Declarative description of the flow's triggers and ordered steps.
    /// May be mutated at startup when schedule overrides (pause/cron override) are applied.
    /// </summary>
    FlowManifest Manifest { get; set; }
}
