using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// DeadEndSkipDemoFlow — Demonstrates run-level <c>Failed</c> status when an entry step crashes
/// and all downstream steps become Blocked (Skipped) because their <c>runAfter</c> conditions
/// can never be satisfied.
///
/// A real step crash (<c>validate_input</c> → Failed) is never masked by downstream skip
/// propagation. The run is recorded as <c>Failed</c> even though every other step is Skipped.
///
/// DAG:
///   validate_input  (SimulatedFailure → always crashes)
///       └─► enrich_data   (RunAfter: validate_input=[Succeeded]) → Blocked
///                 └─► save_result (RunAfter: enrich_data=[Succeeded])  → Blocked
///
/// Expected run result:
///   validate_input   Failed   ← real crash
///   enrich_data      Skipped  ← Blocked because validate_input did not Succeed
///   save_result      Skipped  ← Blocked because enrich_data did not Succeed
///   → run status = Failed  (crash takes precedence over downstream blocking)
/// </summary>
public sealed class DeadEndSkipDemoFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000010");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            ["validate_input"] = new StepMetadata
            {
                Type = "SimulatedFailure",
                Inputs = new Dictionary<string, object?>
                {
                    ["reason"] = "Input validation failed — required fields missing."
                }
            },

            ["enrich_data"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["validate_input"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?> { ["message"] = "Enriching data from external source." }
            },

            // Leaf step — only Succeeded is accepted, so Blocked propagates here.
            // All downstream Skipped + entry Failed → run-level = Failed.
            ["save_result"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["enrich_data"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?> { ["message"] = "Saving enriched result to store." }
            }
        }
    };
}
