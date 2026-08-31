using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// WarehouseRobotFlow — a warehouse scanning job driven by a real (simulated) robot.
///
/// This is the exact production shape reported in issue #169, promoted to a first-class
/// sample: a <c>ForEach</c> over storage locations whose body <b>parks mid-iteration</b> on a
/// <c>WaitForSignal</c> until the robot reports it has arrived, then reads that robot's
/// payload via a scope-relative <c>@steps()</c> expression — and a callback step that must
/// run only after <b>every</b> location has been scanned.
///
/// What it demonstrates, per step:
///
///   scan_start              — @triggerBody() expression on a webhook/manual trigger.
///   scan_process            — ForEach fan-out over @triggerBody()?.Locations with
///                             ConcurrencyLimit = 1 (one location at a time, like one robot).
///     wait_robot_goto       — WaitForSignal ("robot_goto"): the iteration parks until the
///                             robot arrives. The loop step stays Running on the dashboard
///                             the whole time — the v1.30.1 loop completion barrier.
///     open_camera           — reads THIS iteration's robot payload by bare sibling key:
///                             @steps('wait_robot_goto').output.Location (v1.30.0, issue #166).
///   robot_callback_success  — RunAfter = { scan_process: [Succeeded] }: dispatched only when
///                             the barrier settles, i.e. after the last location is scanned
///                             (v1.30.1, issue #169). Reads the loop's own output
///                             (@steps('scan_process').output.iterations) for the summary.
///
/// How to run it "like production":
///   The RobotSimulatorHostedService plays the robot — it notices each parked
///   wait_robot_goto waiter and delivers the "robot_goto" signal a few seconds later with a
///   realistic payload, so a single trigger plays the whole job out on the dashboard with no
///   manual signalling. Watch the run detail: iterations light up one by one while
///   scan_process stays Running, and robot_callback_success only appears at the very end.
///
/// Trigger it:
///   Manual — dashboard "Trigger" button, body:
///     { "OrderNo": "WH-1042", "Locations": ["A-01-03", "A-02-07", "B-11-01"] }
///   Webhook —
///     POST /flows/api/webhook/warehouse-scan
///     Content-Type: application/json
///     X-Webhook-Key: warehouse-scan-secret
///     { "OrderNo": "WH-1042", "Locations": ["A-01-03", "A-02-07", "B-11-01"] }
///
/// To drive the robot yourself instead, set ROBOT_SIMULATOR=false and POST the signals:
///   POST /flows/api/runs/{runId}/signals/robot_goto   body: { "Location": "A-01-03" }
/// </summary>
public sealed class WarehouseRobotFlow : IFlowDefinition
{
    /// <summary>Stable flow identifier (…0013).</summary>
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000013");

    /// <summary>Schema version.</summary>
    public string Version => "1.0";

    /// <summary>Trigger + step manifest — see the type-level remarks for the walkthrough.</summary>
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
            ["scan_location"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "warehouse-scan",
                    // Enforced independently of the webhook-hardening pipeline's Audit mode:
                    // callers must send "X-Webhook-Key: warehouse-scan-secret" (or a Bearer
                    // token with the same value) or the endpoint returns 401. Sample-only
                    // value — production secrets belong in configuration, not source.
                    ["webhookSecret"] = "warehouse-scan-secret"
                }
            }
        },
        Steps = new StepCollection
        {
            // Entry: acknowledge the scan job. Nothing depends on its output — it exists so
            // the run timeline shows when the job was accepted.
            ["scan_start"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()?.OrderNo"
                }
            },

            // One iteration per storage location. ConcurrencyLimit = 1 models a single robot:
            // dispatch is staggered, and each iteration parks until ITS robot_goto arrives.
            ["scan_process"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["scan_start"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.Locations",
                ConcurrencyLimit = 1,
                Steps = new StepCollection
                {
                    // Parks in Pending until the robot reports arrival. All iterations share the
                    // signal name — per-iteration routing comes from the runtime step key
                    // (scan_process.{index}.wait_robot_goto). Times out after 5 minutes so an
                    // abandoned job fails loudly instead of parking forever.
                    ["wait_robot_goto"] = new StepMetadata
                    {
                        Type = "WaitForSignal",
                        Inputs = new Dictionary<string, object?>
                        {
                            ["signalName"] = "robot_goto",
                            ["timeoutSeconds"] = 300
                        }
                    },

                    // Captures the location the robot ACTUALLY reached — read from this
                    // iteration's own waiter output by bare sibling key.
                    ["open_camera"] = new StepMetadata
                    {
                        Type = "OpenCamera",
                        RunAfter = new RunAfterCollection { ["wait_robot_goto"] = [StepStatus.Succeeded] },
                        Inputs = new Dictionary<string, object?>
                        {
                            ["OrderNo"] = "@triggerBody()?.OrderNo",
                            ["Location"] = "@steps('wait_robot_goto').output.Location"
                        }
                    }
                }
            },

            // Runs after ALL iterations complete — the loop completion barrier guarantees it.
            // Reads the loop step's own output for the iteration count.
            ["robot_callback_success"] = new StepMetadata
            {
                Type = "RobotCallback",
                RunAfter = new RunAfterCollection { ["scan_process"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["OrderNo"] = "@triggerBody()?.OrderNo",
                    ["ScannedCount"] = "@steps('scan_process').output.iterations"
                }
            }
        }
    };
}
