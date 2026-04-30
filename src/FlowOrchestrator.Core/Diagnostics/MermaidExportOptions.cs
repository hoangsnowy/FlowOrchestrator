namespace FlowOrchestrator.Core.Diagnostics;

/// <summary>
/// Configuration knobs for <see cref="FlowMermaidExporter"/>.
/// </summary>
public sealed class MermaidExportOptions
{
    /// <summary>
    /// Diagram direction passed to Mermaid's <c>flowchart</c> header.
    /// Valid values are <c>TD</c> (top-down), <c>LR</c> (left-right), <c>BT</c>, and <c>RL</c>.
    /// </summary>
    public string Direction { get; set; } = "TD";

    /// <summary>
    /// When <see langword="true"/>, emits one trigger node per entry in
    /// <see cref="Abstractions.FlowManifest.Triggers"/> and connects it to all entry steps.
    /// </summary>
    public bool IncludeTriggers { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, the step's <see cref="Abstractions.StepMetadata.Type"/>
    /// is rendered as italic text below the step key inside each node label.
    /// </summary>
    public bool ShowStepTypes { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, emits <c>classDef</c> blocks and applies
    /// <c>:::trigger</c>, <c>:::entry</c>, <c>:::polling</c>, and <c>:::loop</c>
    /// classes to differentiate node roles visually.
    /// </summary>
    public bool ApplyStyling { get; set; } = true;
}
