using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

public sealed class OrderProcessingFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000002");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = "Manual" }
        },
        Steps = new StepCollection
        {
            ["fetch_orders"] = new StepMetadata
            {
                Type = "QueryDatabase",
                Inputs = new Dictionary<string, object?>
                {
                    ["sql"] = "SELECT TOP 10 Id, CustomerName, Total FROM Orders WHERE Status = @Status",
                    ["parameters"] = new Dictionary<string, object?> { ["Status"] = "Pending" }
                }
            },
            ["enrich_data"] = new StepMetadata
            {
                Type = "CallExternalApi",
                RunAfter = new RunAfterCollection { ["fetch_orders"] = ["Succeeded"] },
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"] = "/posts/1"
                }
            },
            ["save_result"] = new StepMetadata
            {
                Type = "SaveResult",
                RunAfter = new RunAfterCollection { ["enrich_data"] = ["Succeeded"] },
                Inputs = new Dictionary<string, object?>
                {
                    ["table"] = "ProcessedResults"
                }
            }
        }
    };
}
