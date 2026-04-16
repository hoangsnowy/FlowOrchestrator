using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Deserializes a webhook payload into a typed model and extracts key business fields.
///
/// ── Advanced topic: Typed webhook payload deserialization + TriggerData fallback ─
///
/// This step demonstrates two complementary patterns for working with webhook data:
///
///   1. Trigger expression as input
///      The flow manifest passes "@triggerBody()" as the Payload input:
///
///        ["payload"] = "@triggerBody()"
///
///      At runtime FlowOrchestrator resolves this to the raw JsonElement of the
///      webhook body before calling ExecuteAsync. The step never needs to touch
///      IOutputsRepository or IExecutionContext to read the trigger data.
///
///   2. ctx.TriggerData fallback
///      If Payload is null (e.g. the step is triggered without an expression),
///      the handler falls back to ctx.TriggerData — the same payload stored in
///      IOutputsRepository under the "__trigger" key. This makes the step testable
///      in isolation: trigger the flow manually with a body and the step still works.
///
///   3. Typed deserialization
///      JsonSerializer.Deserialize{PaymentEventEnvelope} converts the raw JSON into
///      a strongly-typed model so downstream logic uses C# properties, not string
///      navigation. ParseError is captured when deserialization fails so the run
///      can succeed with diagnostic output instead of throwing.
///
/// Expected payload (from PaymentEventFlow):
/// {
///   "payload": { "id": "pay_abc123", "orderId": "ord_456", "amount": 99.99, "status": "confirmed" },
///   "event":   "payment.confirmed",
///   "timestamp": "2026-04-16T10:00:00Z"
/// }
///
/// Used by: PaymentEventFlow → parse_payment_payload step
/// </summary>
public sealed class SerializeProbeStep : IStepHandler<SerializeProbeStepInput>
{
    private readonly ILogger<SerializeProbeStep> _logger;

    public SerializeProbeStep(ILogger<SerializeProbeStep> logger) => _logger = logger;

    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<SerializeProbeStepInput> step)
    {
        // Pattern: use the expression-resolved input first; fall back to TriggerData.
        var source = step.Inputs.Payload ?? ctx.TriggerData;
        var json   = SerializePayload(source, step.Inputs.Indented);

        var parsed = TryDeserializeEnvelope(json, out var envelope, out var parseError);

        var result = new SerializeProbeStepResult
        {
            Json           = json,
            Parsed         = parsed,
            ParseError     = parseError,
            PaymentId      = envelope?.Payload?.Id,
            OrderId        = envelope?.Payload?.OrderId,
            EventType      = envelope?.Event,
            Timestamp      = envelope?.Timestamp
        };

        _logger.LogInformation(
            "[SerializeProbe] RunId={RunId} Step={StepKey} Parsed={Parsed} PaymentId={PaymentId} OrderId={OrderId} Event={Event}",
            ctx.RunId, step.Key, result.Parsed,
            result.PaymentId ?? "<null>",
            result.OrderId   ?? "<null>",
            result.EventType ?? "<null>");

        return ValueTask.FromResult<object?>(new StepResult<SerializeProbeStepResult>
        {
            Key   = step.Key,
            Value = result
        });
    }

    private static string SerializePayload(object? payload, bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };

        return payload switch
        {
            null                                                           => "null",
            JsonElement { ValueKind: JsonValueKind.Null
                       or JsonValueKind.Undefined }                        => "null",
            JsonElement el                                                  => el.GetRawText(),
            _                                                               => JsonSerializer.Serialize(payload, payload.GetType(), options)
        };
    }

    private static bool TryDeserializeEnvelope(string json, out PaymentEventEnvelope? envelope, out string? error)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<PaymentEventEnvelope>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            envelope = null;
            error    = ex.Message;
            return false;
        }
    }
}

public sealed class SerializeProbeStepInput
{
    /// <summary>
    /// The payload to deserialize. Typically bound via "@triggerBody()" in the manifest.
    /// Falls back to ctx.TriggerData when null.
    /// </summary>
    public object? Payload { get; set; }

    /// <summary>Pretty-print the JSON output when true.</summary>
    public bool Indented { get; set; }
}

public sealed class SerializeProbeStepResult
{
    /// <summary>The serialized JSON string passed to this step.</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>True when the JSON was successfully deserialized into PaymentEventEnvelope.</summary>
    public bool Parsed { get; set; }

    /// <summary>Deserialization error message, or null on success.</summary>
    public string? ParseError { get; set; }

    /// <summary>Extracted from payload.id — e.g. "pay_abc123".</summary>
    public string? PaymentId { get; set; }

    /// <summary>Extracted from payload.orderId — e.g. "ord_456".</summary>
    public string? OrderId { get; set; }

    /// <summary>Extracted from event — e.g. "payment.confirmed".</summary>
    public string? EventType { get; set; }

    /// <summary>Extracted from timestamp — e.g. "2026-04-16T10:00:00Z".</summary>
    public string? Timestamp { get; set; }
}

// ── Typed model matching the PaymentEventFlow webhook payload ────────────────
// Adjust these classes if your payment gateway uses a different envelope schema.

public sealed class PaymentEventEnvelope
{
    public PaymentEventPayload? Payload   { get; set; }
    public string?              Event     { get; set; }
    public string?              Timestamp { get; set; }
}

public sealed class PaymentEventPayload
{
    public string?  Id      { get; set; }
    public string?  OrderId { get; set; }
    public decimal? Amount  { get; set; }
    public string?  Status  { get; set; }
}
