namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Optional hint returned by a step handler to instruct the engine to spawn
/// dynamic child steps (e.g. ForEach iterations) after the handler completes.
/// </summary>
/// <remarks>
/// <see cref="Spawn"/> must NOT contain step keys that appear in the static DAG.
/// Hints are reserved for dynamic fan-out only; use <c>runAfter</c> in the manifest
/// for static dependencies. The engine validates and throws on violation.
/// </remarks>
/// <param name="Spawn">Dynamic child steps to enqueue after the parent step succeeds.</param>
public sealed record StepDispatchHint(IReadOnlyList<StepDispatchRequest> Spawn);

/// <summary>
/// Describes a single dynamic child step to be dispatched by the engine
/// when a handler returns a <see cref="StepDispatchHint"/>.
/// </summary>
/// <param name="StepKey">The unique runtime key for the child step (e.g. <c>"parent.0.child"</c>).</param>
/// <param name="StepType">The type name used to look up the registered handler.</param>
/// <param name="Inputs">Initial input values for the child step.</param>
/// <param name="Delay">Optional scheduling delay; <see langword="null"/> means enqueue immediately.</param>
public sealed record StepDispatchRequest(
    string StepKey,
    string StepType,
    IDictionary<string, object?> Inputs,
    TimeSpan? Delay = null);
