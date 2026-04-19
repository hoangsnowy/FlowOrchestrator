using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Serialization;

/// <summary>
/// Custom <see cref="JsonConverter{T}"/> for <see cref="StepMetadata"/> that dispatches
/// to <see cref="LoopStepMetadata"/> when the JSON <c>type</c> property equals <c>ForEach</c>,
/// and manually deserializes base <see cref="StepMetadata"/> properties otherwise to avoid
/// re-entering this converter recursively.
/// </summary>
public sealed class StepMetadataJsonConverter : JsonConverter<StepMetadata>
{
    /// <summary>
    /// Reads a <see cref="StepMetadata"/> or <see cref="LoopStepMetadata"/> from JSON.
    /// </summary>
    /// <param name="reader">The JSON reader positioned at the start of the object.</param>
    /// <param name="typeToConvert">Always <see cref="StepMetadata"/>.</param>
    /// <param name="options">Serializer options forwarded to nested deserializations.</param>
    /// <returns>
    /// A <see cref="LoopStepMetadata"/> when <c>type</c> is <c>ForEach</c>;
    /// otherwise a base <see cref="StepMetadata"/>.
    /// </returns>
    public override StepMetadata? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;

        if (string.Equals(type, "ForEach", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Deserialize<LoopStepMetadata>(root.GetRawText(), options);
        }

        // Deserialize base StepMetadata manually to avoid re-entering this converter.
        var step = new StepMetadata();
        if (type is not null)
            step.Type = type;
        if (root.TryGetProperty("runAfter", out var runAfterProp))
            step.RunAfter = JsonSerializer.Deserialize<RunAfterCollection>(runAfterProp.GetRawText(), options) ?? new();
        if (root.TryGetProperty("inputs", out var inputsProp))
            step.Inputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputsProp.GetRawText(), options) ?? new();

        return step;
    }

    /// <summary>
    /// Writes a <see cref="StepMetadata"/> or <see cref="LoopStepMetadata"/> to JSON,
    /// routing loop steps through their concrete type so all loop-specific properties are included.
    /// </summary>
    /// <param name="writer">The JSON writer to write to.</param>
    /// <param name="value">The step metadata to serialize.</param>
    /// <param name="options">Serializer options forwarded to nested serializations.</param>
    public override void Write(Utf8JsonWriter writer, StepMetadata value, JsonSerializerOptions options)
    {
        if (value is LoopStepMetadata loop)
        {
            // Serialize as LoopStepMetadata (not StepMetadata) so this converter is not re-invoked
            // for the outer object. Nested StepMetadata values inside loop.Steps will be handled
            // correctly when the dictionary serializer calls back into this converter per-entry.
            JsonSerializer.Serialize(writer, loop, options);
            return;
        }

        // Write base StepMetadata properties manually to avoid re-entering this converter.
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WritePropertyName("runAfter");
        JsonSerializer.Serialize(writer, value.RunAfter, options);
        writer.WritePropertyName("inputs");
        JsonSerializer.Serialize(writer, value.Inputs, options);
        writer.WriteEndObject();
    }
}

