using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Minimal one-step manual flow used by the disabled-flow and trigger-expression
/// regression tests. The handler bumps an injected <see cref="DisabledFlowInvocationProbe"/>
/// so tests can assert it ran (or did not run) without mocking the engine.
/// </summary>
public sealed class SimpleManualFlow : IFlowDefinition
{
    public Guid Id { get; } = new("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["work"] = new StepMetadata { Type = "Probe" }
        }
    };
}

/// <summary>Singleton probe that records every invocation of <see cref="ProbeStepHandler"/>.</summary>
public sealed class DisabledFlowInvocationProbe
{
    private int _calls;
    public int Calls => _calls;
    public int Increment() => Interlocked.Increment(ref _calls);
}

/// <summary>Handler that bumps the shared probe and returns success.</summary>
public sealed class ProbeStepHandler : IStepHandler
{
    private readonly DisabledFlowInvocationProbe _probe;
    public ProbeStepHandler(DisabledFlowInvocationProbe probe) => _probe = probe;

    public ValueTask<object?> ExecuteAsync(IExecutionContext context, IFlowDefinition flow, IStepInstance step)
    {
        _probe.Increment();
        return ValueTask.FromResult<object?>(new StepResult { Key = step.Key });
    }
}
