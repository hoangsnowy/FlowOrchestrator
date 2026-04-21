using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// ConditionalSkipDemoFlow — Demonstrates the <see cref="StepStatus.Skipped"/> status.
///
/// This flow models a payment authorisation scenario where validation intentionally fails,
/// causing the "happy path" branch to be skipped while the "fallback" branch runs instead.
///
/// DAG:
///   start
///     └─► validate_payment         (SimulatedFailure → always fails)
///           ├─► charge_customer     (RunAfter: validate_payment=[Succeeded]) → SKIPPED
///           └─► handle_decline      (RunAfter: validate_payment=[Failed])    → runs
///                 └─► send_receipt  (RunAfter: charge_customer=[Succeeded|Skipped],
///                                             handle_decline=[Succeeded|Skipped]) → always runs
///
/// Expected run result:
///   start              Succeeded
///   validate_payment   Failed
///   charge_customer    Skipped   ← the step whose CSS / badge this demo exercises
///   handle_decline     Succeeded
///   send_receipt       Succeeded
/// </summary>
public sealed class ConditionalSkipDemoFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000008");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual }
        },
        Steps = new StepCollection
        {
            // ── Entry ──────────────────────────────────────────────────────────
            ["start"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "ConditionalSkipDemo — initiating payment authorisation..."
                }
            },

            // ── Simulated failure ──────────────────────────────────────────────
            // Always throws → status becomes Failed → triggers the skip below.
            ["validate_payment"] = new StepMetadata
            {
                Type = "SimulatedFailure",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["reason"] = "Payment gateway returned: card declined (insufficient funds)."
                }
            },

            // ── Happy path (will be SKIPPED) ───────────────────────────────────
            // validate_payment failed, so this condition can never be satisfied.
            ["charge_customer"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["validate_payment"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Charging customer — authorisation approved."
                }
            },

            // ── Fallback path (runs because validate_payment Failed) ────────────
            ["handle_decline"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["validate_payment"] = [StepStatus.Failed]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Payment declined — notifying customer and logging event."
                }
            },

            // ── Always runs regardless of which branch executed ─────────────────
            // Accepts Succeeded OR Skipped from both branches so it always fires.
            ["send_receipt"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["charge_customer"] = [StepStatus.Succeeded, StepStatus.Skipped],
                    ["handle_decline"]  = [StepStatus.Succeeded, StepStatus.Skipped]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Receipt dispatched — payment flow complete."
                }
            }
        }
    };
}
