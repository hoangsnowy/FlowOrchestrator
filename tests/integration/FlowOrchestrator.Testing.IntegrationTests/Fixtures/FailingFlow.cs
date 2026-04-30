using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Single-step flow whose handler always throws — exercises the engine's exception capture path.
/// Used by <see cref="FailureTests"/>.
/// </summary>
public sealed class FailingFlow : IFlowDefinition
{
    public Guid Id { get; } = new("22222222-2222-2222-2222-222222222222");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["boom"] = new StepMetadata { Type = "Boom" }
        }
    };
}

/// <summary>Handler that throws <see cref="InvalidOperationException"/> with message <c>"boom"</c> on every call.</summary>
public sealed class BoomStepHandler : IStepHandler
{
    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step) =>
        throw new InvalidOperationException("boom");
}
