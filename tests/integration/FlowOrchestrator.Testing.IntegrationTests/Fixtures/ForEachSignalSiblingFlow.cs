using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Testing.Tests.Fixtures;

/// <summary>
/// Replicates the exact manifest shape reported in issue #166: a ForEach loop
/// (<c>scan_process</c>) whose body pairs a <c>WaitForSignal</c> child with a consumer child
/// that reads the waiter's output by <b>bare sibling key</b>:
/// <c>open_camera.Location = @steps('wait_robot_goto').output.Location</c>.
/// </summary>
/// <remarks>
/// All iterations wait on the same signal name (<c>robot_goto</c>), exactly as in the report —
/// per-iteration routing comes from the runtime step key (<c>scan_process.{index}.wait_robot_goto</c>),
/// which is also what the scope-relative <c>@steps()</c> rewrite must target.
/// </remarks>
public sealed class ForEachSignalSiblingFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier.</summary>
    public Guid Id { get; } = new("44444444-4444-4444-4444-444444444444");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest mirroring the issue #166 configuration.</summary>
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["scan_process"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.Steps",
                ConcurrencyLimit = 1,
                Steps = new StepCollection
                {
                    ["wait_robot_goto"] = new StepMetadata
                    {
                        Type = "WaitForSignal",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["signalName"] = "robot_goto"
                        }
                    },
                    ["open_camera"] = new StepMetadata
                    {
                        Type = "Echo",
                        RunAfter = new RunAfterCollection { ["wait_robot_goto"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?>
                        {
                            // The bare sibling reference from the issue — must resolve to
                            // scan_process.{index}.wait_robot_goto, i.e. THIS iteration's waiter.
                            ["label"] = "@steps('wait_robot_goto').output.Location"
                        }
                    }
                }
            }
        }
    };
}
