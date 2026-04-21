using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// A demo-only step handler that always throws an exception, causing the step to transition
/// to <see cref="StepStatus.Failed"/>. Used by <c>ConditionalSkipDemoFlow</c> to reliably
/// trigger the skip behaviour in downstream steps whose <c>runAfter</c> condition requires
/// <see cref="StepStatus.Succeeded"/>.
/// </summary>
public sealed class SimulatedFailureStep : IStepHandler<SimulatedFailureStepInput>
{
    private readonly ILogger<SimulatedFailureStep> _logger;

    /// <summary>Initializes a new instance of <see cref="SimulatedFailureStep"/>.</summary>
    public SimulatedFailureStep(ILogger<SimulatedFailureStep> logger) => _logger = logger;

    /// <summary>
    /// Always throws <see cref="InvalidOperationException"/> so the step is recorded as
    /// <see cref="StepStatus.Failed"/>, causing any downstream step that requires
    /// <see cref="StepStatus.Succeeded"/> to be skipped.
    /// </summary>
    public ValueTask<object?> ExecuteAsync(
        IExecutionContext ctx,
        IFlowDefinition flow,
        IStepInstance<SimulatedFailureStepInput> step)
    {
        _logger.LogWarning(
            "[SimulatedFailure] RunId={RunId} Step={StepKey} — intentionally failing: {Reason}",
            ctx.RunId, step.Key, step.Inputs.Reason);

        throw new InvalidOperationException(step.Inputs.Reason ?? "Simulated failure for demo purposes.");
    }
}

/// <summary>Input for <see cref="SimulatedFailureStep"/>.</summary>
public sealed class SimulatedFailureStepInput
{
    /// <summary>Human-readable reason included in the thrown exception message.</summary>
    public string? Reason { get; set; }
}
