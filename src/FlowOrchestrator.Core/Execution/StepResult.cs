using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Serialization;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// General-purpose <see cref="IStepResult"/> implementation for step handlers
/// that return untyped output or no output at all.
/// </summary>
public sealed class StepResult : IStepResult
{
    /// <inheritdoc/>
    public string Key { get; set; } = default!;

    /// <inheritdoc/>
    public StepStatus Status { get; set; } = StepStatus.Succeeded;

    /// <inheritdoc/>
    public object? Result { get; set; }

    /// <inheritdoc/>
    public string? FailedReason { get; set; }

    /// <inheritdoc/>
    public bool ReThrow { get; set; }

    /// <inheritdoc/>
    public TimeSpan? DelayNextStep { get; set; }

    /// <inheritdoc/>
    public StepDispatchHint? DispatchHint { get; set; }
}

/// <summary>
/// Typed <see cref="IStepResult"/> that carries a strongly-typed <see cref="Value"/>.
/// The <see cref="IStepResult.Result"/> property round-trips through JSON conversion
/// so downstream steps can retrieve the value regardless of how the result was stored.
/// </summary>
/// <typeparam name="T">The CLR type of the step output value.</typeparam>
public sealed class StepResult<T> : IStepResult
{
    /// <inheritdoc/>
    public string Key { get; set; } = default!;

    /// <inheritdoc/>
    public StepStatus Status { get; set; } = StepStatus.Succeeded;

    /// <summary>Strongly-typed output value produced by the step.</summary>
    public T? Value { get; set; }

    /// <inheritdoc/>
    public string? FailedReason { get; set; }

    /// <inheritdoc/>
    public bool ReThrow { get; set; }

    /// <inheritdoc/>
    public TimeSpan? DelayNextStep { get; set; }

    /// <inheritdoc/>
    public StepDispatchHint? DispatchHint { get; set; }

    /// <summary>
    /// Gets or sets <see cref="Value"/> via JSON round-trip conversion.
    /// The setter accepts any raw value (e.g. a <see cref="System.Text.Json.JsonElement"/>)
    /// and deserialises it to <typeparamref name="T"/>.
    /// </summary>
    public object? Result
    {
        get => Value;
        set => Value = JsonValueConversion.Deserialize<T>(value);
    }
}
