using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Three-step linear test flow: <c>step_a</c> → <c>step_b</c> → <c>step_c</c>.
/// Each step type maps to <see cref="EchoStepHandler"/> which echoes its <c>label</c> input back as output.
/// Used by <see cref="HappyPathTests"/>.
/// </summary>
public sealed class LinearTestFlow : IFlowDefinition
{
    public Guid Id { get; } = new("11111111-1111-1111-1111-111111111111");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["step_a"] = new StepMetadata
            {
                Type = "Echo",
                Inputs = new Dictionary<string, object?> { ["label"] = "alpha" }
            },
            ["step_b"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["step_a"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "beta" }
            },
            ["step_c"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["step_b"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["label"] = "gamma" }
            }
        }
    };
}

/// <summary>Inputs for <see cref="EchoStepHandler"/>.</summary>
public sealed class EchoStepInput
{
    public string? Label { get; set; }
}

/// <summary>Outputs of <see cref="EchoStepHandler"/>.</summary>
public sealed class EchoStepOutput
{
    public string? Echoed { get; set; }
}

/// <summary>
/// Test handler that returns its <c>label</c> input back as <c>echoed</c> output.
/// Allows <see cref="HappyPathTests"/> to assert end-to-end input → output flow.
/// </summary>
public sealed class EchoStepHandler : IStepHandler<EchoStepInput>
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance<EchoStepInput> step) =>
        ValueTask.FromResult<object?>(new StepResult<EchoStepOutput>
        {
            Key = step.Key,
            Value = new EchoStepOutput { Echoed = step.Inputs.Label }
        });
}
