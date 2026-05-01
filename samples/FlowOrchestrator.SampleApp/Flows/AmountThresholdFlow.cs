using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// AmountThresholdFlow — Demonstrates the <c>When</c> boolean condition on <c>RunAfter</c>.
///
/// Two branches both depend on <c>start</c> succeeding. Each branch's <c>When</c> clause
/// references the trigger payload to decide whether it should run:
///
///   • high_value_approve runs if <c>@triggerBody().amount &gt; 1000</c> (Skipped otherwise)
///   • auto_approve       runs if <c>@triggerBody().amount &lt;= 1000</c> (Skipped otherwise)
///
/// The terminal <c>complete</c> step accepts both Succeeded and Skipped from each branch,
/// so the run always finishes successfully regardless of which branch took effect.
///
/// Try it from the dashboard:
///   • Trigger with body <c>{ "amount": 1500 }</c> → high_value_approve runs, auto_approve Skipped.
///   • Trigger with body <c>{ "amount": 500 }</c>  → auto_approve runs, high_value_approve Skipped.
///
/// On a Skipped step, the dashboard shows the evaluation trace
/// (e.g. <c>500 &gt; 1000 → false</c>) under the "Why skipped" panel.
/// </summary>
public sealed class AmountThresholdFlow : IFlowDefinition
{
    /// <inheritdoc/>
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000012");

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <inheritdoc/>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "amount-threshold" }
            }
        },
        Steps = new StepCollection
        {
            // ── Entry — surfaces the incoming amount in the run timeline ───────
            ["start"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody().amount"
                }
            },

            // ── High-value branch — runs when amount > 1000 ────────────────────
            ["high_value_approve"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody().amount > 1000"
                    }
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "High-value transaction — routing to manual approval queue."
                }
            },

            // ── Default branch — runs when amount <= 1000 ──────────────────────
            ["auto_approve"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["start"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody().amount <= 1000"
                    }
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Standard transaction — auto-approved."
                }
            },

            // ── Terminal — accepts Succeeded OR Skipped from both branches ─────
            ["complete"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["high_value_approve"] = [StepStatus.Succeeded, StepStatus.Skipped],
                    ["auto_approve"]       = [StepStatus.Succeeded, StepStatus.Skipped]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Transaction processing complete."
                }
            }
        }
    };
}
