using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// ApprovalWorkflowFlow — Demonstrates the built-in <c>WaitForSignal</c> step type for
/// human-in-the-loop workflows.
///
/// Flow shape:
///   submit_request          → LogMessage (records the incoming request)
///   wait_for_approval       → WaitForSignal (parks until the dashboard / API delivers a signal)
///   notify_approver         → LogMessage (uses the signal payload via @steps('wait_for_approval'))
///
/// To trigger a manual run from the dashboard at <c>/flows</c>: click "Trigger". To deliver the
/// signal, open the run page and click "Send Signal" on the parked <c>wait_for_approval</c> step,
/// or POST directly:
/// <code>
/// curl -X POST http://localhost:&lt;port&gt;/flows/api/runs/{runId}/signals/approval \
///      -H "Content-Type: application/json" \
///      -d '{"approved":true,"approver":"manager@example.com"}'
/// </code>
/// </summary>
public sealed class ApprovalWorkflowFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000007");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["submit_request"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Approval request submitted; awaiting manager review."
                }
            },
            ["wait_for_approval"] = new StepMetadata
            {
                Type = "WaitForSignal",
                RunAfter = new RunAfterCollection { ["submit_request"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["signalName"] = "approval",
                    ["timeoutSeconds"] = 86400  // 24 hours
                }
            },
            ["notify_approver"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["wait_for_approval"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@steps('wait_for_approval').output.approver"
                }
            }
        }
    };
}
