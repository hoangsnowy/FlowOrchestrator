using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// DeadEndSkipDemoFlow — Produces a run-level <c>Skipped</c> status.
///
/// The entry step always fails and all downstream steps require <c>Succeeded</c>,
/// so skip propagates to every leaf — nothing useful runs — and the run is recorded
/// as <c>Skipped</c> rather than <c>Failed</c> or <c>Succeeded</c>.
///
/// DAG:
///   validate_input  (SimulatedFailure → always fails)
///       └─► enrich_data   (RunAfter: validate_input=[Succeeded]) → Skipped
///                 └─► save_result (RunAfter: enrich_data=[Succeeded])   → Skipped ← leaf
///
/// Expected run result:
///   validate_input   Failed
///   enrich_data      Skipped
///   save_result      Skipped  ← only leaf, all leaves Skipped → run = Skipped
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

            // Leaf step — only Succeeded is accepted, so skip propagates here.
            // All leaves Skipped → run-level = Skipped.
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
