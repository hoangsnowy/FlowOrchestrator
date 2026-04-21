using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// SkipVariantsDemoFlow — Demonstrates two distinct Skipped step scenarios in a single run.
///
/// DAG:
///   start
///     └─► check_subscription    (SimulatedFailure → always fails)
///           ├─► apply_discount   (RunAfter: check_subscription=[Succeeded]) → SKIPPED (middle)
///           │     └─► send_confirmation (RunAfter: apply_discount=[Succeeded])  → SKIPPED (end)
///           │
///           └─► log_check_failed (RunAfter: check_subscription=[Failed])     → Succeeded
///                 └─► finalize   (RunAfter: apply_discount=[Succeeded|Skipped],
///                                           log_check_failed=[Succeeded|Skipped]) → Succeeded
///
/// Expected run result:
///   start                Succeeded
///   check_subscription   Failed
///   apply_discount       Skipped  ← MIDDLE skip: chain tiếp tục qua nhánh khác
///   send_confirmation    Skipped  ← END skip: dead-end, không có gì chạy sau
///   log_check_failed     Succeeded
///   finalize             Succeeded
/// </summary>
public sealed class SkipVariantsDemoFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000009");
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
                    ["message"] = "SkipVariantsDemo — verifying subscription status..."
                }
            },

            // ── Intentional failure ────────────────────────────────────────────
            ["check_subscription"] = new StepMetadata
            {
                Type = "SimulatedFailure",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["reason"] = "Subscription not found for this account."
                }
            },

            // ── MIDDLE skip ────────────────────────────────────────────────────
            // Skipped because check_subscription Failed.
            // Flow does NOT stop here — finalize still runs via log_check_failed.
            ["apply_discount"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["check_subscription"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Applying loyalty discount to order."
                }
            },

            // ── END skip (dead-end) ────────────────────────────────────────────
            // Skipped because apply_discount was Skipped.
            // Nothing depends on this step → this is the terminal Skipped node.
            ["send_confirmation"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["apply_discount"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Sending discount confirmation email to customer."
                }
            },

            // ── Fallback path ──────────────────────────────────────────────────
            ["log_check_failed"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["check_subscription"] = [StepStatus.Failed]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Subscription check failed — proceeding without discount."
                }
            },

            // ── Always runs ────────────────────────────────────────────────────
            // Accepts Skipped from apply_discount and Succeeded from log_check_failed.
            ["finalize"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["apply_discount"]   = [StepStatus.Succeeded, StepStatus.Skipped],
                    ["log_check_failed"] = [StepStatus.Succeeded, StepStatus.Skipped]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Order finalized — flow complete."
                }
            }
        }
    };
}
