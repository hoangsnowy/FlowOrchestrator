namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Thrown when an <c>@steps()</c> expression references a step key that is not declared
/// in the flow manifest, indicating an authoring error in the flow definition.
/// </summary>
public sealed class FlowExpressionException : Exception
{
    /// <summary>The raw expression string that triggered the exception.</summary>
    public string Expression { get; }

    /// <summary>The step key that could not be found in the flow manifest.</summary>
    public string StepKey { get; }

    /// <summary>Initialises a new instance with the offending expression, step key, and error message.</summary>
    public FlowExpressionException(string expression, string stepKey, string message)
        : base(message)
    {
        Expression = expression;
        StepKey = stepKey;
    }

    /// <summary>Initialises a new instance wrapping an inner exception.</summary>
    public FlowExpressionException(string expression, string stepKey, string message, Exception inner)
        : base(message, inner)
    {
        Expression = expression;
        StepKey = stepKey;
    }
}
