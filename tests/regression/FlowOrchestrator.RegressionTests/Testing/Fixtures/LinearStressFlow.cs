using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Three-step linear flow (<c>step_a</c> → <c>step_b</c> → <c>step_c</c>) used to stress
/// the run-termination ordering. Mirrors the integration-test <c>LinearTestFlow</c> but
/// duplicated locally so the regression project doesn't need to reference the integration
/// test assembly.
/// </summary>
public sealed class LinearStressFlow : IFlowDefinition
{
    public Guid Id { get; } = new("dddddddd-3333-3333-3333-dddddddddddd");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["step_a"] = new StepMetadata { Type = "StressEcho" },
            ["step_b"] = new StepMetadata
            {
                Type = "StressEcho",
                RunAfter = new RunAfterCollection { ["step_a"] = [StepStatus.Succeeded] }
            },
            ["step_c"] = new StepMetadata
            {
                Type = "StressEcho",
                RunAfter = new RunAfterCollection { ["step_b"] = [StepStatus.Succeeded] }
            }
        }
    };
}

/// <summary>Trivial handler used by <see cref="LinearStressFlow"/>; returns immediately.</summary>
public sealed class StressEchoStepHandler : IStepHandler
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step) =>
        ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
}
