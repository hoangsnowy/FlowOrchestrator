using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

public sealed class WebhookTriggerBodyTestFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000004");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "trigger-body-test"
                }
            }
        },
        Steps = new StepCollection
        {
            ["log_full_payload"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()"
                }
            },
            ["serialize_probe"] = new StepMetadata
            {
                Type = "SerializeProbe",
                RunAfter = new RunAfterCollection { ["log_full_payload"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["payload"] = "@triggerBody()",
                    ["indented"] = true
                }
            },
            ["log_payload_id"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["serialize_probe"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()?.payload.id"
                }
            }
        }
    };
}
