namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Captures the evaluation of a <c>When</c> boolean expression for diagnostic display.
/// Persisted alongside the step record when a step is skipped because its
/// <c>When</c> clause evaluated to <see langword="false"/>.
/// </summary>
/// <remarks>
/// The <see cref="Resolved"/> string is a human-readable rewrite of <see cref="Expression"/>
/// with each LHS reference replaced by its actual resolved value, so a reader can see
/// exactly why the condition failed (e.g. <c>500 &gt; 1000</c>).
/// </remarks>
public sealed class WhenEvaluationTrace
{
    /// <summary>The original expression text from the flow manifest.</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>The expression rewritten with every <c>@steps()</c>/<c>@triggerBody()</c> reference replaced by its resolved value.</summary>
    public string Resolved { get; set; } = string.Empty;

    /// <summary>The boolean outcome of the evaluation.</summary>
    public bool Result { get; set; }
}
