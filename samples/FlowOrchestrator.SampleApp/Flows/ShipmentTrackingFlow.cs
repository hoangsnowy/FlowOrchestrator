using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// ShipmentTrackingFlow — Polls a carrier API until a shipment status is confirmed.
///
/// This flow demonstrates the PollableStepHandler pattern. After an order is fulfilled,
/// this flow can be triggered to monitor the shipment with a carrier tracking endpoint.
/// It retries at intervals until the carrier confirms the shipment (pollConditionPath matches),
/// then logs the outcome.
///
/// Trigger this flow manually from the dashboard and observe the Runs timeline:
/// the check_shipment_status step will show Pending → Pending → Succeeded progression,
/// and the Step Attempts panel will record each poll attempt's input/output.
///
/// Steps:
///   check_shipment_status → CallExternalApi — polls carrier API (min 3 attempts, every 5s)
///   log_shipment_confirmed → LogMessage    — logs confirmation when tracking resolves
/// </summary>
public sealed class ShipmentTrackingFlow : IFlowDefinition
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
            // Poll the carrier tracking endpoint until the shipment ID field equals 1.
            // Minimum 3 attempts ensures you can observe the Pending state in the dashboard.
            // In production, replace path with a real tracking API and adjust the condition.
            ["check_shipment_status"] = new StepMetadata
            {
                Type = "CallExternalApi",
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"] = "/posts/1",           // Simulates carrier tracking endpoint
                    ["pollEnabled"] = true,
                    ["pollMinAttempts"] = 3,          // Force at least 3 attempts so Pending is visible
                    ["pollIntervalSeconds"] = 5,
                    ["pollTimeoutSeconds"] = 90,
                    ["pollConditionPath"] = "id",     // Carrier returns { "id": 1, ... } on success
                    ["pollConditionEquals"] = 1
                }
            },

            // Log the success message once tracking is confirmed.
            ["log_shipment_confirmed"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["check_shipment_status"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Shipment tracking confirmed — delivery is in transit."
                }
            }
        }
    };
}
