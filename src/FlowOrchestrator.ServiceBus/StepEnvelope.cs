using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.ServiceBus;

/// <summary>
/// Wire-format DTO carried inside a Service Bus step message body.
/// Serialised with System.Text.Json. Only <see cref="FlowId"/> is sent — the consumer
/// rehydrates the full <c>IFlowDefinition</c> from <c>IFlowRepository</c> (mirrors the
/// Hangfire adapter's pattern of avoiding manifest serialisation).
/// </summary>
internal sealed class StepEnvelope
{
    /// <summary>The flow this step belongs to.</summary>
    [JsonPropertyName("flowId")]
    public Guid FlowId { get; set; }

    /// <summary>The run identifier from the execution context.</summary>
    [JsonPropertyName("runId")]
    public Guid RunId { get; set; }

    /// <summary>Optional principal id from the execution context.</summary>
    [JsonPropertyName("principalId")]
    public string? PrincipalId { get; set; }

    /// <summary>Trigger payload (deserialised on the consumer side).</summary>
    [JsonPropertyName("triggerData")]
    public JsonElement? TriggerData { get; set; }

    /// <summary>Trigger headers.</summary>
    [JsonPropertyName("triggerHeaders")]
    public Dictionary<string, string>? TriggerHeaders { get; set; }

    /// <summary>Step manifest key.</summary>
    [JsonPropertyName("stepKey")]
    public string StepKey { get; set; } = string.Empty;

    /// <summary>Step handler type name.</summary>
    [JsonPropertyName("stepType")]
    public string StepType { get; set; } = string.Empty;

    /// <summary>Wall-clock time the step was enqueued.</summary>
    [JsonPropertyName("scheduledTime")]
    public DateTimeOffset ScheduledTime { get; set; }

    /// <summary>Resolved step inputs.</summary>
    [JsonPropertyName("inputs")]
    public Dictionary<string, JsonElement>? Inputs { get; set; }

    /// <summary>Loop iteration index, when applicable.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Internal copy of trigger data carried as a raw object — populated when reading
    /// the envelope back into an <see cref="IExecutionContext"/>. Not serialised.</summary>
    [JsonIgnore]
    public object? RawTriggerData { get; set; }

    /// <summary>Builds an envelope from the live engine arguments. Trigger data is reduced to a
    /// JSON tree so the body remains polymorphism-safe across worker processes.</summary>
    public static StepEnvelope From(IExecutionContext ctx, Guid flowId, IStepInstance step)
    {
        var inputs = new Dictionary<string, JsonElement>(step.Inputs.Count, StringComparer.Ordinal);
        foreach (var (k, v) in step.Inputs)
        {
            inputs[k] = v is null
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonSerializer.SerializeToElement(v);
        }

        return new StepEnvelope
        {
            FlowId = flowId,
            RunId = ctx.RunId,
            PrincipalId = ctx.PrincipalId,
            TriggerData = ctx.TriggerData is null ? null : JsonSerializer.SerializeToElement(ctx.TriggerData),
            TriggerHeaders = ctx.TriggerHeaders is null
                ? null
                : new Dictionary<string, string>(ctx.TriggerHeaders, StringComparer.OrdinalIgnoreCase),
            StepKey = step.Key,
            StepType = step.Type,
            ScheduledTime = step.ScheduledTime,
            Inputs = inputs,
            Index = step.Index,
        };
    }

    /// <summary>Materialises the envelope back into a mutable <see cref="FlowOrchestrator.Core.Execution.ExecutionContext"/>.</summary>
    public FlowOrchestrator.Core.Execution.ExecutionContext ToExecutionContext()
    {
        return new FlowOrchestrator.Core.Execution.ExecutionContext
        {
            RunId = RunId,
            PrincipalId = PrincipalId,
            TriggerData = TriggerData?.Clone(),
            TriggerHeaders = TriggerHeaders is null ? null : new Dictionary<string, string>(TriggerHeaders, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Materialises the envelope back into a mutable <see cref="StepInstance"/>.</summary>
    public StepInstance ToStepInstance()
    {
        var instance = new StepInstance(StepKey, StepType)
        {
            RunId = RunId,
            ScheduledTime = ScheduledTime,
            Index = Index,
        };
        if (Inputs is not null)
        {
            foreach (var (k, el) in Inputs)
            {
                // Undefined and Null both map to a null input — Undefined arises when a producer
                // hand-crafts an envelope with a missing JSON property; calling .Clone() on it
                // throws InvalidOperationException, which would loop the message through SB
                // abandon-redeliver until dead-letter. Treat the same as null.
                instance.Inputs[k] = el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : (object)el.Clone();
            }
        }
        return instance;
    }
}
