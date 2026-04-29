using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// OrderFulfillmentFlow — Processes pending orders end-to-end.
///
/// This is the core business flow of the OrderHub sample. It can be triggered manually
/// from the dashboard or via an external webhook (e.g. from a scheduler or upstream system).
///
/// Steps:
///   fetch_orders   → QueryDatabase      — loads the top 10 pending orders from SQL
///   submit_to_wms  → CallExternalApi    — submits the batch to the Warehouse Management System
///                                         (polling enabled: waits for the WMS job to complete)
///   save_result    → SaveResult         — persists the WMS confirmation to the database
///
/// Webhook: POST /flows/api/webhook/order-fulfillment
///   Header: X-Webhook-Key: your-secret-key
/// </summary>
public sealed class OrderFulfillmentFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000002");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "order-fulfillment",
                    ["webhookSecret"] = "your-secret-key"
                }
            }
        },
        Steps = new StepCollection
        {
            // Step 1: Fetch the oldest 10 pending orders from the local DB.
            ["fetch_orders"] = new StepMetadata
            {
                Type = "QueryDatabase",
                Inputs = new Dictionary<string, object?>
                {
                    ["sql"] = "SELECT TOP 10 Id, CustomerName, Total FROM Orders WHERE Status = @Status ORDER BY CreatedAt ASC",
                    ["parameters"] = new Dictionary<string, object?> { ["Status"] = OrderStatus.Pending.ToString() }
                }
            },

            // Step 2: Submit the order batch to the WMS and poll until the job is accepted.
            // The WMS API returns { "id": 1, "status": "accepted" } when ready.
            // pollConditionPath targets the "id" field; when it equals 1 the step succeeds.
            ["submit_to_wms"] = new StepMetadata
            {
                Type = "CallExternalApi",
                RunAfter = new RunAfterCollection { ["fetch_orders"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"] = "/posts/1",           // Replace with real WMS endpoint in production
                    ["pollEnabled"] = true,
                    ["pollIntervalSeconds"] = 10,
                    ["pollTimeoutSeconds"] = 120,
                    ["pollConditionPath"] = "id"
                }
            },

            // Step 3: Save the WMS confirmation to the ProcessedOrders table.
            // @steps() expressions wire the upstream outputs directly into the handler's
            // input properties — no IOutputsRepository injection needed in SaveResultStep.
            ["save_result"] = new StepMetadata
            {
                Type = "SaveResult",
                RunAfter = new RunAfterCollection { ["submit_to_wms"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["table"]         = "ProcessedOrders",
                    ["fetchedOrders"] = "@steps('fetch_orders').output",
                    ["apiResult"]     = "@steps('submit_to_wms').output"
                }
            }
        }
    };
}
