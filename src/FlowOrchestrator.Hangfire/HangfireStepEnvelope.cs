using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// The single argument of a Hangfire step job: identifiers, the step's resolved inputs and its
/// scheduling data — nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Before 2.0 a step job carried the whole <see cref="IExecutionContext"/> and <see cref="IStepInstance"/>
/// as interface-typed arguments, so Hangfire's Newtonsoft.Json argument store wrote <c>$type</c>
/// metadata for them, the trigger body and headers travelled twice (once on each object), and any
/// user payload value was deserialised under the job store's <c>TypeNameHandling</c>.
/// </para>
/// <para>
/// The envelope mirrors the Service Bus runtime's slim message: trigger data and headers are not
/// carried at all — the engine re-reads both from <c>IOutputsRepository</c> on every step execution —
/// and the inputs travel as one System.Text.Json string (<see cref="InputsJson"/>). Newtonsoft.Json
/// only ever sees that string, so keys such as <c>"$type"</c> inside a ForEach item or trigger-derived
/// input are inert data, never type metadata. The worker rebuilds the inputs as plain JSON-shaped CLR
/// values (<see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/>, <see langword="null"/>).
/// </para>
/// <para>
/// The engine's exactly-once guarantees are unaffected: the claim guard keys on
/// (<see cref="RunId"/>, <see cref="StepKey"/>), both of which the envelope carries verbatim.
/// </para>
/// </remarks>
public sealed class HangfireStepEnvelope
{
    /// <summary>The flow that owns the step; resolved on the worker through <c>FlowRegistry</c>.</summary>
    public Guid FlowId { get; set; }

    /// <summary>The run the step belongs to.</summary>
    public Guid RunId { get; set; }

    /// <summary>The principal that started the run, when one was recorded on the execution context.</summary>
    public string? PrincipalId { get; set; }

    /// <summary>The step's manifest key, or its runtime loop path (e.g. <c>"loop.3.child"</c>).</summary>
    public string StepKey { get; set; } = string.Empty;

    /// <summary>The handler type name the step is mapped to.</summary>
    public string StepType { get; set; } = string.Empty;

    /// <summary>Wall-clock time the step was planned; feeds the queue-delay metric.</summary>
    public DateTimeOffset ScheduledTime { get; set; }

    /// <summary>Zero-based loop iteration index, when the step runs inside a ForEach scope.</summary>
    public int Index { get; set; }

    /// <summary>
    /// The step's inputs as a System.Text.Json object, or <see langword="null"/> when the step has none.
    /// </summary>
    public string? InputsJson { get; set; }

    /// <summary>Builds the envelope for <paramref name="step"/> of <paramref name="flowId"/> in the run described by <paramref name="context"/>.</summary>
    /// <param name="context">The dispatching execution context; only its run id and principal are kept.</param>
    /// <param name="flowId">The flow that owns the step.</param>
    /// <param name="step">The step to run; its inputs are serialised with System.Text.Json.</param>
    /// <returns>A new envelope ready to hand to <c>IBackgroundJobClient</c>.</returns>
    public static HangfireStepEnvelope From(IExecutionContext context, Guid flowId, IStepInstance step)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(step);

        return new HangfireStepEnvelope
        {
            FlowId = flowId,
            RunId = context.RunId,
            PrincipalId = context.PrincipalId,
            StepKey = step.Key,
            StepType = step.Type,
            ScheduledTime = step.ScheduledTime,
            Index = step.Index,
            InputsJson = SerializeInputs(step.Inputs),
        };
    }

    /// <summary>Rebuilds the execution context; trigger data and headers are left for the engine to load from the store.</summary>
    /// <param name="jobId">The Hangfire background-job id, recorded on the context for diagnostics.</param>
    /// <returns>A fresh mutable execution context.</returns>
    public Core.Execution.ExecutionContext ToExecutionContext(string? jobId) => new()
    {
        RunId = RunId,
        PrincipalId = PrincipalId,
        JobId = jobId,
    };

    /// <summary>Rebuilds the step instance, materialising <see cref="InputsJson"/> into plain CLR values.</summary>
    /// <returns>A fresh mutable step instance.</returns>
    /// <exception cref="JsonException"><see cref="InputsJson"/> is not a JSON object.</exception>
    public StepInstance ToStepInstance()
    {
        var step = new StepInstance(StepKey, StepType)
        {
            RunId = RunId,
            PrincipalId = PrincipalId,
            ScheduledTime = ScheduledTime,
            Index = Index,
        };

        if (string.IsNullOrEmpty(InputsJson))
        {
            return step;
        }

        using var document = JsonDocument.Parse(InputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Hangfire step envelope for '{StepKey}' carries inputs that are not a JSON object.");
        }

        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            inputs[property.Name] = Materialize(property.Value);
        }

        step.Inputs = inputs;
        return step;
    }

    private static string? SerializeInputs(IDictionary<string, object?>? inputs)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in inputs)
            {
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Same value shapes ForEachSourceResolver produces for loop items, so a handler sees identical
    // input types whether a value was materialised by the ForEach planner or by this envelope.
    private static object? Materialize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // Cast keeps long vs double; a bare ternary would unify both arms to double.
                return element.TryGetInt64(out var integer) ? integer : (object)element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = Materialize(property.Value);
                }
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(Materialize(item));
                }
                return list;
            default:
                return null;
        }
    }
}
