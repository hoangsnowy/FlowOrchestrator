namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Marks a <see cref="StepMetadata"/> as a scoped container (e.g. a loop)
/// that owns a nested <see cref="StepCollection"/> executed per iteration.
/// </summary>
public interface IScopedStep
{
    /// <summary>
    /// Child steps executed within each iteration of the scope.
    /// Keys are resolved at runtime with the iteration index injected as a numeric segment
    /// (e.g. <c>"processItem.0.validate"</c>).
    /// </summary>
    StepCollection Steps { get; set; }

    /// <summary>The type name of the scope handler (e.g. <c>"foreach"</c>).</summary>
    string Type { get; set; }
}
