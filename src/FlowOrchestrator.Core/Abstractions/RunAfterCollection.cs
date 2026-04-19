namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Maps predecessor step names to the set of <see cref="StepStatus"/> values
/// that allow the dependent step to proceed.
/// An empty collection means the step has no prerequisites (entry step).
/// </summary>
/// <example>
/// <code>
/// { "fetchData": [Succeeded], "validate": [Succeeded, Skipped] }
/// </code>
/// means this step runs only if <c>fetchData</c> succeeded AND <c>validate</c> either succeeded or was skipped.
/// </example>
public sealed class RunAfterCollection : Dictionary<string, StepStatus[]>
{
}
