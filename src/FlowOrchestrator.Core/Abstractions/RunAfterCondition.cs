using System.Collections;
using System.Runtime.CompilerServices;
using FlowOrchestrator.Core.Serialization;
using System.Text.Json.Serialization;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// A single entry inside a <see cref="RunAfterCollection"/>: combines the legacy
/// status-based gate (<see cref="Statuses"/>) with an optional boolean expression
/// (<see cref="When"/>) that is evaluated against the run's outputs at planning time.
/// </summary>
/// <remarks>
/// The <c>[CollectionBuilder]</c> attribute together with the <see cref="Create"/> factory
/// keeps the historical collection-expression syntax compiling unchanged:
/// <code>RunAfter = new RunAfterCollection { ["validate"] = [StepStatus.Succeeded] }</code>
/// In that path the array literal is folded into a condition with only <see cref="Statuses"/> set.
/// </remarks>
[CollectionBuilder(typeof(RunAfterCondition), nameof(Create))]
[JsonConverter(typeof(RunAfterConditionJsonConverter))]
// CRITICAL: Newtonsoft attribute is the only reliable way to override Newtonsoft's
// auto-detection of IEnumerable<StepStatus> as a JSON array. Hangfire serialises job
// arguments via Newtonsoft and may build contracts before our settings are applied.
[Newtonsoft.Json.JsonConverter(typeof(RunAfterConditionNewtonsoftConverter))]
public sealed class RunAfterCondition : IEnumerable<StepStatus>
{
    /// <summary>
    /// The set of predecessor statuses that satisfy this gate. <see langword="null"/> or empty
    /// means the gate is purely expression-based and any predecessor status is accepted.
    /// </summary>
    public StepStatus[]? Statuses { get; set; }

    /// <summary>
    /// Optional boolean expression evaluated at planning time. When present and evaluates to
    /// <see langword="false"/>, the dependent step transitions to <see cref="StepStatus.Skipped"/>
    /// and a <see cref="Expressions.WhenEvaluationTrace"/> is persisted to the step record.
    /// </summary>
    /// <remarks>
    /// Supported operators: <c>==</c>, <c>!=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>&gt;=</c>, <c>&lt;=</c>,
    /// <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, parentheses. Operands are literals (number, string,
    /// <c>true</c>, <c>false</c>, <c>null</c>) and <c>@steps()</c> / <c>@triggerBody()</c> /
    /// <c>@triggerHeaders()</c> expressions.
    /// </remarks>
    public string? When { get; set; }

    /// <summary>Creates a condition from a collection-expression literal of statuses.</summary>
    public static RunAfterCondition Create(ReadOnlySpan<StepStatus> statuses)
        => new() { Statuses = statuses.ToArray() };

    /// <summary>Implicit conversion from a <see cref="StepStatus"/> array — preserves <c>StepStatus[]</c> assignment.</summary>
    public static implicit operator RunAfterCondition(StepStatus[] statuses)
        => new() { Statuses = statuses };

    /// <summary>Returns <see langword="true"/> when <paramref name="status"/> is allowed by <see cref="Statuses"/> (or there is no status gate).</summary>
    public bool AcceptsStatus(StepStatus status)
        => Statuses is null || Statuses.Length == 0 || Array.IndexOf(Statuses, status) >= 0;

    /// <inheritdoc/>
    public IEnumerator<StepStatus> GetEnumerator()
        => ((IEnumerable<StepStatus>)(Statuses ?? Array.Empty<StepStatus>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
