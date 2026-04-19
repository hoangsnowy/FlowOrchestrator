using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// ParallelHealthCheckFlow — Pings three downstream APIs simultaneously and
/// reports the aggregate result after all checks complete.
///
/// ── What this flow demonstrates ──────────────────────────────────────────────
///
/// M1.1 DAG Graph Planner (multiple entry steps — parallel fan-out):
///   FlowOrchestrator's <c>FlowGraphPlanner</c> evaluates the full dependency
///   graph after every step completes. When multiple steps have an empty
///   <c>RunAfter</c>, they are all "Ready" at trigger time and enqueued
///   simultaneously as independent Hangfire jobs.
///
///   Here, <c>ping_orders_api</c>, <c>ping_users_api</c>, and <c>ping_catalog_api</c>
///   are three entry steps — they all start at the same time.
///
/// M1.2 Completion semantics (all-of join with partial failure tolerance):
///   <c>report_health</c> lists all three ping steps in its RunAfter with
///   [Succeeded, Skipped, Failed]. This means the aggregation step always runs
///   regardless of individual ping outcomes. If one endpoint is down, the others
///   still complete and the report still fires.
///
///   Compare to a strict join (Succeeded only): if any ping fails, <c>report_health</c>
///   would be Skipped because its RunAfter condition could never be satisfied.
///
/// M1.3 Graph validation (tested at startup):
///   FlowGraphPlanner.Validate() runs during FlowSyncHostedService.StartAsync.
///   It confirms:
///     • At least one entry step exists (three here).
///     • All RunAfter references resolve to declared steps.
///     • No dependency cycles exist.
///   A misconfigured RunAfter would throw at startup before any job is enqueued.
///
/// ── Steps ────────────────────────────────────────────────────────────────────
///
///   ping_orders_api   → CallExternalApi  ┐
///   ping_users_api    → CallExternalApi  ├── All three enqueued simultaneously (entry steps)
///   ping_catalog_api  → CallExternalApi  ┘
///         │                  │                  │
///         └──────────────────┴──────────────────┘
///                            ▼
///   report_health     → LogMessage  — Runs after all three complete (any terminal status)
///
/// ── Dashboard observation ────────────────────────────────────────────────────
///
///   Trigger this flow manually from the dashboard and watch the Runs timeline:
///   the three ping steps will appear with overlapping start times, confirming
///   they ran in parallel. report_health will start only after the last ping finishes.
/// </summary>
public sealed class ParallelHealthCheckFlow : IFlowDefinition
{
    /// <inheritdoc/>
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000006");

    /// <inheritdoc/>
    public string Version => "1.0";

    /// <inheritdoc/>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            // Manual trigger — run from the dashboard to observe parallel execution.
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },

            // Optional cron: run a health check every 5 minutes in production.
            // Uncomment and remove the comment block to activate.
            // ["scheduled"] = new TriggerMetadata
            // {
            //     Type = TriggerType.Cron,
            //     Inputs = new Dictionary<string, object?> { ["cronExpression"] = "*/5 * * * *" }
            // }
        },

        Steps = new StepCollection
        {
            // ── Entry steps: three independent health checks running in parallel ──
            //
            // All three have an empty RunAfter — FlowGraphPlanner treats them as
            // simultaneous entry points and enqueues all three as separate Hangfire jobs
            // when TriggerAsync fires. None waits for the others.

            // Ping 1: Orders service
            ["ping_orders_api"] = new StepMetadata
            {
                Type = "CallExternalApi",
                // RunAfter is intentionally empty (omitted = no dependency).
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"]   = "/posts/1",   // Simulates an Orders API health endpoint
                    // pollEnabled is false (default) — execute once, succeed or fail immediately.
                }
            },

            // Ping 2: Users service
            ["ping_users_api"] = new StepMetadata
            {
                Type = "CallExternalApi",
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"]   = "/posts/2"    // Simulates a Users API health endpoint
                }
            },

            // Ping 3: Catalog service
            ["ping_catalog_api"] = new StepMetadata
            {
                Type = "CallExternalApi",
                Inputs = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["path"]   = "/posts/3"    // Simulates a Catalog API health endpoint
                }
            },

            // ── Aggregation step: waits for all three pings (all-of join) ─────────
            //
            // RunAfter lists all three entry steps. Each allows Succeeded, Skipped, AND Failed,
            // so this step always runs regardless of individual ping outcomes.
            //
            // Why include Failed?
            //   A real health-check reporter should run even when some endpoints are down —
            //   it needs to record the failure, not be silently skipped.
            //   If you want strict success (abort if any fails), remove Failed from each list.
            //
            // How the planner resolves this:
            //   FlowGraphPlanner.Evaluate() checks statuses after each ping completes.
            //   report_health moves from Waiting → Ready only when all three pings
            //   have a terminal status that satisfies their respective RunAfter conditions.
            ["report_health"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    // Accept any terminal status from each ping — report always fires.
                    ["ping_orders_api"]  = [StepStatus.Succeeded, StepStatus.Skipped, StepStatus.Failed],
                    ["ping_users_api"]   = [StepStatus.Succeeded, StepStatus.Skipped, StepStatus.Failed],
                    ["ping_catalog_api"] = [StepStatus.Succeeded, StepStatus.Skipped, StepStatus.Failed],
                },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "Health check cycle complete — see step outputs for individual results."
                }
            }
        }
    };
}
