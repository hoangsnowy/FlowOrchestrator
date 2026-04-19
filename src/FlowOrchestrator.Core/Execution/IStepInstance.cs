namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Represents a single, concrete invocation of a step within a flow run,
/// carrying its resolved inputs and the ambient execution context.
/// </summary>
/// <typeparam name="TInput">
/// The type of the step's input model. For untyped steps this is
/// <see cref="IDictionary{TKey,TValue}"/> of <c>string</c> to <c>object?</c>.
/// </typeparam>
public interface IStepInstance<TInput> : IExecutionContext
{
    /// <summary>The wall-clock time at which this step was enqueued.</summary>
    DateTimeOffset ScheduledTime { get; set; }

    /// <summary>The type name that maps this step to its registered <see cref="IStepHandler"/>.</summary>
    string Type { get; set; }

    /// <summary>
    /// The unique key that identifies this step in the flow manifest (or its runtime loop path,
    /// e.g. <c>"processItems.0.validate"</c>).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Step inputs after expression resolution. For typed steps, this is deserialised to
    /// <typeparamref name="TInput"/> before the handler is invoked.
    /// </summary>
    TInput Inputs { get; set; }

    /// <summary>
    /// Zero-based iteration index when this step is executing inside a
    /// <see cref="FlowOrchestrator.Core.Abstractions.LoopStepMetadata"/> scope.
    /// </summary>
    int Index { get; set; }

    /// <summary>
    /// When <see langword="true"/>, signals the loop handler to advance to the next iteration
    /// after this step completes. Set by <c>ForEachStepHandler</c>.
    /// </summary>
    bool ScopeMoveNext { get; set; }
}

/// <summary>
/// Untyped <see cref="IStepInstance{TInput}"/> variant using a raw input dictionary.
/// Used internally when the handler is untyped or inputs have not yet been deserialised.
/// </summary>
public interface IStepInstance : IStepInstance<IDictionary<string, object?>> { }
