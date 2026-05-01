using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Two-step flow used by the WaitForSignal happy-path test:
/// <c>wait_for_approval</c> (WaitForSignal) → <c>finalize</c> (Echo).
/// </summary>
public sealed class WaitForSignalFlow : IFlowDefinition
{
    public Guid Id { get; } = new("aaaa1111-aaaa-1111-aaaa-111111111111");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["wait_for_approval"] = new StepMetadata
            {
                Type = "WaitForSignal",
                Inputs = new Dictionary<string, object?>
                {
                    ["signalName"] = "approval"
                }
            },
            ["finalize"] = new StepMetadata
            {
                Type = "Echo",
                RunAfter = new RunAfterCollection { ["wait_for_approval"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["label"] = "@steps('wait_for_approval').output.approver"
                }
            }
        }
    };
}

/// <summary>
/// Two parallel WaitForSignal steps with different signal names. Used to verify that delivering
/// signal "alpha" only resumes the matching waiter.
/// </summary>
public sealed class WaitForSignalParallelFlow : IFlowDefinition
{
    public Guid Id { get; } = new("aaaa2222-aaaa-2222-aaaa-222222222222");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["wait_alpha"] = new StepMetadata
            {
                Type = "WaitForSignal",
                Inputs = new Dictionary<string, object?> { ["signalName"] = "alpha" }
            },
            ["wait_beta"] = new StepMetadata
            {
                Type = "WaitForSignal",
                Inputs = new Dictionary<string, object?> { ["signalName"] = "beta" }
            }
        }
    };
}

/// <summary>
/// Single WaitForSignal step with a short timeout configured. Used by the timeout test —
/// no signal arrives so the step transitions to Failed.
/// </summary>
public sealed class WaitForSignalTimeoutFlow : IFlowDefinition
{
    public Guid Id { get; } = new("aaaa3333-aaaa-3333-aaaa-333333333333");
    public string Version => "1.0";

    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["wait"] = new StepMetadata
            {
                Type = "WaitForSignal",
                Inputs = new Dictionary<string, object?>
                {
                    ["signalName"] = "approval",
                    ["timeoutSeconds"] = 1
                }
            }
        }
    };
}
