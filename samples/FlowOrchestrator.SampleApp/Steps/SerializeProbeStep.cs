using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Demonstrates serialization/deserialization from complex webhook payloads.
/// Inputs:
/// - payload (optional): object/json to serialize. If null, falls back to ctx.TriggerData.
/// - indented (optional): pretty-print JSON output.
/// </summary>
public sealed class SerializeProbeStep : IStepHandler<SerializeProbeStepInput>
{
    private readonly ILogger<SerializeProbeStep> _logger;

    public SerializeProbeStep(ILogger<SerializeProbeStep> logger)
    {
        _logger = logger;
    }

    public ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance<SerializeProbeStepInput> step)
    {
        var source = step.Inputs.Payload ?? ctx.TriggerData;
        var json = SerializePayload(source, step.Inputs.Indented);

        var parsed = TryDeserializeEnvelope(json, out var envelope, out var parseError);
        var result = new SerializeProbeStepResult
        {
            Json = json,
            Parsed = parsed,
            ParseError = parseError,
            EventId = envelope?.Id,
            EventType = envelope?.Type,
            PayloadId = envelope?.Payload?.Id,
            TenantId = envelope?.Payload?.TenantId
        };

        _logger.LogInformation(
            "[SerializeProbe] RunId={RunId} Step={StepKey} Parsed={Parsed} EventId={EventId} PayloadId={PayloadId}",
            ctx.RunId,
            step.Key,
            result.Parsed,
            result.EventId ?? "<null>",
            result.PayloadId ?? "<null>");

        return ValueTask.FromResult<object?>(new StepResult<SerializeProbeStepResult>
        {
            Key = step.Key,
            Value = result
        });
    }

    private static string SerializePayload(object? payload, bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented
        };

        if (payload is null)
        {
            return "null";
        }

        if (payload is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return "null";
            }

            try
            {
                return element.GetRawText();
            }
            catch (InvalidOperationException)
            {
                return JsonSerializer.Serialize(element.ToString(), options);
            }
        }

        return JsonSerializer.Serialize(payload, payload.GetType(), options);
    }

    private static bool TryDeserializeEnvelope(string json, out ProbeWebhookEnvelope? envelope, out string? error)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<ProbeWebhookEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            envelope = null;
            error = ex.Message;
            return false;
        }
    }
}

public sealed class SerializeProbeStepInput
{
    public object? Payload { get; set; }
    public bool Indented { get; set; }
}

public sealed class SerializeProbeStepResult
{
    public string Json { get; set; } = string.Empty;
    public bool Parsed { get; set; }
    public string? ParseError { get; set; }
    public string? EventId { get; set; }
    public string? EventType { get; set; }
    public string? PayloadId { get; set; }
    public string? TenantId { get; set; }
}

public sealed class ProbeWebhookEnvelope
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public ProbeWebhookPayload? Payload { get; set; }
}

public sealed class ProbeWebhookPayload
{
    public string? Id { get; set; }
    public string? TenantId { get; set; }
}
