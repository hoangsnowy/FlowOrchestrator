using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Maps predecessor step names to a <see cref="RunAfterCondition"/> describing
/// when the dependent step is allowed to proceed.
/// An empty collection means the step has no prerequisites (entry step).
/// </summary>
/// <remarks>
/// Backwards-compatible: the legacy syntax <c>["fetch"] = [StepStatus.Succeeded]</c>
/// continues to work via <see cref="RunAfterCondition.Create"/>. Authors may now also write
/// <c>["fetch"] = new RunAfterCondition { Statuses = [Succeeded], When = "@steps('fetch').output.amount &gt; 1000" }</c>.
/// </remarks>
/// <example>
/// <code>
/// new RunAfterCollection
/// {
///     ["fetchData"] = [StepStatus.Succeeded],
///     ["validate"]  = new RunAfterCondition
///     {
///         Statuses = [StepStatus.Succeeded],
///         When     = "@steps('validate').output.score &gt;= 0.8"
///     }
/// }
/// </code>
/// </example>
[JsonConverter(typeof(RunAfterCollectionJsonConverter))]
public sealed class RunAfterCollection : Dictionary<string, RunAfterCondition>
{
}
