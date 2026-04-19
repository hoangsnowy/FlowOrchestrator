using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// OrderBatchFlow — Processes a batch of order IDs received via webhook, using a ForEach
/// loop to validate each order in parallel, then logs the final summary.
///
/// ── What this flow demonstrates ──────────────────────────────────────────────
///
/// M1.4 ForEach (Loop steps):
///   The <see cref="LoopStepMetadata"/> type enables a step to iterate over a collection
///   and execute a nested <see cref="StepCollection"/> for each element.
///
///   Key fields on LoopStepMetadata:
///     ForEach        — Source collection. Can be:
///                        • A static array:  ["ORD-001", "ORD-002", "ORD-003"]
///                        • An expression:   "@triggerBody()?.orderIds"
///                          (resolved from the trigger payload at execution time)
///     ConcurrencyLimit — Max iterations running in parallel. 1 = sequential, >1 = parallel fan-out.
///     Steps           — Child step definitions. Each child is enqueued as an independent
///                       Hangfire job with the runtime key:
///                         {parentKey}.{zeroBasedIndex}.{childKey}
///                       e.g. "process_orders.0.validate_order"
///                              "process_orders.1.validate_order"  (runs in parallel)
///
/// M1.1 DAG Graph Planner (parallel fan-out within ForEach):
///   With ConcurrencyLimit = 2, FlowGraphPlanner enqueues two iterations simultaneously.
///   The planner evaluates which iteration keys are "Ready" after each child completes
///   and enqueues the next batch when slots open up.
///
/// M2.3 Idempotency:
///   Include an "Idempotency-Key" header when triggering this flow from an external system.
///   FlowOrchestrator deduplicates triggers with the same key — the second call returns
///   the existing runId without creating a new run.
///
/// ── Expected trigger payload ─────────────────────────────────────────────────
///
///   POST /flows/api/webhook/order-batch
///   Content-Type: application/json
///   Idempotency-Key: batch-2026-04-19-001   (optional, prevents duplicate processing)
///
///   {
///     "batchId": "BATCH-001",
///     "orderIds": ["ORD-001", "ORD-002", "ORD-003", "ORD-004"]
///   }
///
/// ── Steps ────────────────────────────────────────────────────────────────────
///
///   prepare_batch    → LogMessage         — Logs the incoming batch ID from trigger data
///   process_orders   → ForEach            — Iterates over orderIds (ConcurrencyLimit = 2)
///     └ validate_order → ProcessOrderItem — Validates and logs each order (per iteration)
///   finalize_batch   → LogMessage         — Logs completion after all iterations finish
///
/// ── Runtime step key layout ──────────────────────────────────────────────────
///
///   prepare_batch
///   process_orders
///     process_orders.0.validate_order    ← iteration 0 (parallel)
///     process_orders.1.validate_order    ← iteration 1 (parallel, ConcurrencyLimit = 2)
///     process_orders.2.validate_order    ← iteration 2 (waits for a slot)
///     process_orders.3.validate_order    ← iteration 3 (waits for a slot)
///   finalize_batch
/// </summary>
public sealed class OrderBatchFlow : IFlowDefinition
{
    /// <inheritdoc/>
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000005");

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <inheritdoc/>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            // Manual trigger for dashboard testing — send any payload to experiment.
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },

            // Webhook trigger: external systems POST a batch payload to start the flow.
            // An "Idempotency-Key" header prevents duplicate processing of the same batch.
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"]   = "order-batch",
                    // Uncomment to require a secret header from the sender:
                    // ["webhookSecret"] = "batch-processing-secret"
                }
            }
        },

        Steps = new StepCollection
        {
            // ── Step 1: Prepare ─────────────────────────────────────────────────
            // Entry step (no RunAfter). Logs the batch ID from the trigger body.
            // @triggerBody()?.batchId resolves the "batchId" field from the JSON payload.
            ["prepare_batch"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()?.batchId"
                }
            },

            // ── Step 2: Process orders — ForEach loop ────────────────────────────
            // LoopStepMetadata marks this as a scoped (loop) step.
            //
            // ForEach = "@triggerBody()?.orderIds":
            //   Resolved at execution time against the trigger payload.
            //   If the payload is { "orderIds": ["ORD-001", "ORD-002"] }, the loop
            //   iterates over ["ORD-001", "ORD-002"] and passes each as __loopItem.
            //
            // ConcurrencyLimit = 2:
            //   FlowGraphPlanner allows up to 2 iterations to be in-flight simultaneously.
            //   Iteration 0 and 1 are enqueued immediately; 2 and 3 wait for a slot.
            //
            // Steps (child):
            //   validate_order uses ProcessOrderItem type — it reads __loopItem (the order ID)
            //   and __loopIndex (the position) injected by ForEachStepHandler.
            ["process_orders"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection
                {
                    ["prepare_batch"] = [StepStatus.Succeeded]
                },

                // Source collection: resolved from the webhook payload at execution time.
                // For manual triggers with no payload, the loop executes 0 iterations
                // (finalize_batch still runs because process_orders completes as Succeeded).
                ForEach = "@triggerBody()?.orderIds",

                // Process 2 orders concurrently. Increase to fan-out further,
                // or set to 1 for strictly sequential iteration.
                ConcurrencyLimit = 2,

                Steps = new StepCollection
                {
                    // Child step: runs once per order ID.
                    // __loopItem and __loopIndex are injected automatically by ForEachStepHandler.
                    // MaxOrderValue is a static constraint — same value for all iterations.
                    ["validate_order"] = new StepMetadata
                    {
                        Type = "ProcessOrderItem",
                        Inputs = new Dictionary<string, object?>
                        {
                            // Static manifest input — merged with __loopItem / __loopIndex
                            // before the handler receives them. Optional: omit to use defaults.
                            ["maxOrderValue"] = 10000
                        }
                    }
                }
            },

            // ── Step 3: Finalize ─────────────────────────────────────────────────
            // Runs after all ForEach iterations complete (process_orders reaches Succeeded).
            // Logs a static completion message — in a real flow this could aggregate
            // results via IOutputsRepository.GetStepOutputAsync for each iteration key.
            ["finalize_batch"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["process_orders"] = [StepStatus.Succeeded]
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Order batch processing complete — all items validated."
                }
            }
        }
    };
}
