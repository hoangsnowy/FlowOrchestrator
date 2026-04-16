using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Flows;

/// <summary>
/// PaymentEventFlow — Handles incoming payment gateway webhook events.
///
/// When a payment is confirmed or rejected, the payment gateway POSTs an event to
/// /flows/api/webhook/payment-event. This flow extracts the payment payload using
/// trigger expressions and logs the key fields.
///
/// Use this flow to explore the @triggerBody() expression system: how to access
/// nested fields from the webhook body and how trigger headers are captured.
///
/// Example webhook payload:
/// {
///   "payload": {
///     "id": "pay_abc123",
///     "orderId": "ord_456",
///     "amount": 99.99,
///     "status": "confirmed"
///   },
///   "event": "payment.confirmed",
///   "timestamp": "2026-04-16T10:00:00Z"
/// }
///
/// Steps:
///   log_payment_received  → LogMessage    — logs the full raw event payload
///   parse_payment_payload → SerializeProbe — deserializes and pretty-prints the payload
///   log_payment_id        → LogMessage    — extracts and logs the payment ID from the body
///
/// Webhook: POST /flows/api/webhook/payment-event
///   No secret required (add webhookSecret to the trigger inputs to secure it)
/// </summary>
public sealed class PaymentEventFlow : IFlowDefinition
{
    public Guid Id { get; } = new Guid("00000000-0000-0000-0000-000000000004");
    public string Version => "1.0";
    public FlowManifest Manifest { get; set; } = new FlowManifest
    {
        Triggers = new FlowTriggerCollection
        {
            ["webhook"] = new TriggerMetadata
            {
                Type = TriggerType.Webhook,
                Inputs = new Dictionary<string, object?>
                {
                    ["webhookSlug"] = "payment-event"
                    // Uncomment to require X-Webhook-Key header from the payment gateway:
                    // ["webhookSecret"] = "payment-gateway-secret"
                }
            }
        },
        Steps = new StepCollection
        {
            // Step 1: Log the entire raw webhook body for visibility.
            // @triggerBody() returns the full JSON payload as received.
            ["log_payment_received"] = new StepMetadata
            {
                Type = "LogMessage",
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()"
                }
            },

            // Step 2: Deserialize the payload envelope and pretty-print it.
            // SerializeProbe parses the outer envelope and extracts the nested payload object.
            ["parse_payment_payload"] = new StepMetadata
            {
                Type = "SerializeProbe",
                RunAfter = new RunAfterCollection { ["log_payment_received"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["payload"] = "@triggerBody()",
                    ["indented"] = true
                }
            },

            // Step 3: Extract the payment ID from the nested payload using a path expression.
            // @triggerBody()?.payload.id navigates into the "payload" object and reads "id".
            ["log_payment_id"] = new StepMetadata
            {
                Type = "LogMessage",
                RunAfter = new RunAfterCollection { ["parse_payment_payload"] = [StepStatus.Succeeded] },
                Inputs = new Dictionary<string, object?>
                {
                    ["message"] = "@triggerBody()?.payload.id"
                }
            }
        }
    };
}
