using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// FinalStepSkipDemoFlow — Demonstrates a <c>Succeeded</c> run where the very last step
/// in a linear chain is <c>Skipped</c> because it is an error-handler that was never needed.
///
/// This is the "happy path with optional final fallback" pattern: the main linear chain
/// completes successfully, so the final step — which only fires when the step directly
/// before it fails — is Skipped. The run still reports <c>Succeeded</c> because no
/// unhandled failure exists.
///
/// DAG (linear chain — no branching):
///   check_inventory ──► reserve_stock ──► confirm_order ──► notify_ops_on_failure
///                                                              RunAfter: confirm_order=[Failed]
///                                                              (Skipped — confirm_order Succeeded)
///
/// Expected run result:
///   check_inventory         Succeeded
///   reserve_stock           Succeeded
///   confirm_order           Succeeded
///   notify_ops_on_failure   Skipped  ← truly final step, error handler at end of chain
///   → run status = Succeeded  (skip at the end is normal; no unhandled failure exists)
/// </summary>
public sealed class FinalStepSkipDemoFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000011");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            // ── Step 1 — Entry ────────────────────────────────────────────────────
            ["check_inventory"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "FinalStepSkipDemo — checking inventory availability..."
                }
            },

            // ── Step 2 ────────────────────────────────────────────────────────────
            ["reserve_stock"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["check_inventory"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Stock reserved successfully."
                }
            },

            // ── Step 3 ────────────────────────────────────────────────────────────
            ["confirm_order"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["reserve_stock"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Order confirmed and dispatched to fulfilment."
                }
            },

            // ── Step 4 — FINAL LEAF (will be SKIPPED) ────────────────────────────
            // Sits at the very end of the linear chain. Only fires when confirm_order
            // fails. Because confirm_order Succeeded, this condition is never met →
            // step is Skipped → run is still Succeeded.
            ["notify_ops_on_failure"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["confirm_order"] = [StepStatus.Failed]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "ALERT: order confirmation failed — ops team notified."
                }
            }
        }
    };
}
