using FlowOrchestrator.Core.Abstractions;

namespace OrderHub;

/// <summary>Stable flow ids; the rehearsal script addresses flows by these.</summary>
public static class FlowIds
{
    public static readonly Guid OrderWebhook = new("0a000000-0000-0000-0000-000000000001");
    public static readonly Guid NightlyReport = new("0a000000-0000-0000-0000-000000000002");
    public static readonly Guid LegacyCleanup = new("0a000000-0000-0000-0000-000000000003");
    public static readonly Guid Approval = new("0a000000-0000-0000-0000-000000000004");
    public static readonly Guid ShipmentTracking = new("0a000000-0000-0000-0000-000000000005");
    public static readonly Guid ShipmentBatch = new("0a000000-0000-0000-0000-000000000006");
    public static readonly Guid Payment = new("0a000000-0000-0000-0000-000000000007");
}

/// <summary>
/// Signed order webhook (GitHub HMAC scheme, delivery-id nonce, replay window), a ForEach over the
/// order lines and a When branch on the order total.
/// </summary>
public sealed class OrderWebhookFlow : IFlowDefinition
{
    public Guid Id => FlowIds.OrderWebhook;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "orders",
                    ["webhookHmacKey"] = "rehearsal-hmac-key",
                    ["webhookSignatureScheme"] = "GitHub",
                    ["webhookNonceHeader"] = "X-GitHub-Delivery",
                    ["webhookTimestampHeader"] = "X-Webhook-Timestamp",
                    ["webhookReplayToleranceSeconds"] = 3600,
                },
            },
        },
        Steps = new StepCollection
        {
            ["validate_order"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "@triggerBody()?.orderId" },
            },
            ["reserve_items"] = new LoopStepMetadata
            {
                Type = "ForEach",
                RunAfter = new RunAfterCollection { ["validate_order"] = [StepStatus.Succeeded] },
                ForEach = "@triggerBody()?.items",
                ConcurrencyLimit = 2,
                Steps = new StepCollection
                {
                    ["reserve"] = new StepMetadata
                    {
                        Type = "LogMessage",
                        Inputs = new Dictionary<string, object?> { ["message"] = "reserving line" },
                    },
                },
            },
            ["route_high_value"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["reserve_items"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody()?.total > 1000",
                    },
                },
                Inputs = new Dictionary<string, object?> { ["message"] = "high value: manual review" },
            },
            ["route_standard"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["reserve_items"] = new RunAfterCondition
                    {
                        Statuses = [StepStatus.Succeeded],
                        When = "@triggerBody()?.total <= 1000",
                    },
                },
                Inputs = new Dictionary<string, object?> { ["message"] = "standard: auto-approved" },
            },
            ["complete"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection
                {
                    ["route_high_value"] = [StepStatus.Succeeded, StepStatus.Skipped],
                    ["route_standard"] = [StepStatus.Succeeded, StepStatus.Skipped],
                },
                Inputs = new Dictionary<string, object?> { ["message"] = "order complete" },
            },
        },
    };
}

/// <summary>Cron flow whose schedule the operator overrides from the dashboard.</summary>
public sealed class NightlyReportFlow : IFlowDefinition
{
    public Guid Id => FlowIds.NightlyReport;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["nightly"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "0 3 * * *" },
            },
        },
        Steps = new StepCollection
        {
            ["build_report"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "nightly report built" },
            },
        },
    };
}

/// <summary>Cron flow the operator paused from the dashboard; it must stay paused.</summary>
public sealed class LegacyCleanupFlow : IFlowDefinition
{
    public Guid Id => FlowIds.LegacyCleanup;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection
        {
            ["every_minute"] = new TriggerMetadata
            {
                Type = TriggerType.Cron,
                Inputs = new Dictionary<string, object?> { ["cronExpression"] = "* * * * *" },
            },
        },
        Steps = new StepCollection
        {
            ["cleanup"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "cleanup ran" },
            },
        },
    };
}

/// <summary>Human approval: WaitForSignal with a per-run timeout, and an escalation branch.</summary>
public sealed class ApprovalFlow : IFlowDefinition
{
    public Guid Id => FlowIds.Approval;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["submit"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?> { ["message"] = "@triggerBody()?.requestId" },
            },
            ["wait_for_approval"] = new StepMetadata
            {
                Type = "WaitForSignal",
                RunAfter = new RunAfterCollection { ["submit"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["signalName"] = "approval",
                    ["timeoutSeconds"] = "@triggerBody()?.timeoutSeconds",
                },
            },
            ["finalize"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["wait_for_approval"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "@steps('wait_for_approval').output.approver" },
            },
            ["escalate"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["wait_for_approval"] = [StepStatus.Failed] },
                Inputs = new Dictionary<string, object?> { ["message"] = "approval timed out: escalating" },
            },
        },
    };
}

/// <summary>Polls a carrier until the shipment is delivered.</summary>
public sealed class ShipmentTrackingFlow : IFlowDefinition
{
    public Guid Id => FlowIds.ShipmentTracking;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["track"] = new StepMetadata
            {
                Type = "CarrierStatus",
                Inputs = CarrierPolling.Inputs("@triggerBody()?.shipmentId"),
            },
            ["notify_customer"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["track"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "shipment delivered" },
            },
        },
    };
}

/// <summary>Tracks several shipments one after the other: a ForEach whose child step polls.</summary>
public sealed class ShipmentBatchFlow : IFlowDefinition
{
    public Guid Id => FlowIds.ShipmentBatch;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["track_all"] = new LoopStepMetadata
            {
                Type = "ForEach",
                ForEach = "@triggerBody()?.shipments",
                ConcurrencyLimit = 1,
                Steps = new StepCollection
                {
                    ["track"] = new StepMetadata { Type = "CarrierStatus", Inputs = CarrierPolling.Inputs(null) },
                },
            },
            ["close_batch"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["track_all"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "batch delivered" },
            },
        },
    };
}

/// <summary>Card payment; a non-positive amount is declined (a failed run). Deduplicated by Idempotency-Key.</summary>
public sealed class PaymentFlow : IFlowDefinition
{
    public Guid Id => FlowIds.Payment;
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new()
    {
        Triggers = new FlowTriggerCollection { ["manual"] = new TriggerMetadata { Type = TriggerType.Manual } },
        Steps = new StepCollection
        {
            ["charge"] = new StepMetadata
            {
                Type = "ChargeCard",
                Inputs = new Dictionary<string, object?> { ["amount"] = "@triggerBody()?.amount" },
            },
            ["send_receipt"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["charge"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?> { ["message"] = "receipt sent" },
            },
        },
    };
}

internal static class CarrierPolling
{
    public static Dictionary<string, object?> Inputs(string? shipmentIdExpression)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["pollEnabled"] = true,
            ["pollIntervalSeconds"] = 30,
            ["pollTimeoutSeconds"] = 3600,
            ["pollConditionPath"] = "status",
            ["pollConditionEquals"] = "delivered",
        };
        if (shipmentIdExpression is not null)
        {
            inputs["shipmentId"] = shipmentIdExpression;
        }
        return inputs;
    }
}
