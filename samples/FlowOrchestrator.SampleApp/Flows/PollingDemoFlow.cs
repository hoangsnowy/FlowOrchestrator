using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

public sealed class PollingDemoFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000003");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
        },
        Steps = new StepCollection
        {
            ["poll_external_status"] = new StepMetadata
            {
                Type = "CallExternalApi",
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"] = "/posts/1",
                    ["pollEnabled"] = true,
                    ["pollMinAttempts"] = 3,
                    ["pollIntervalSeconds"] = 5,
                    ["pollTimeoutSeconds"] = 90,
                    ["pollConditionPath"] = "id",
                    ["pollConditionEquals"] = 1
                }
            },
            ["log_done"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["poll_external_status"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Polling demo completed after retries."
                }
            }
        }
    };
}
