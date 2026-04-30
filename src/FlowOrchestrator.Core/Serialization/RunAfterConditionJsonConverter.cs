using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Serialization;

/// <summary>
/// Reads a <see cref="RunAfterCondition"/> from either the legacy JSON array shape
/// (<c>["Succeeded","Skipped"]</c>) or the new object shape
/// (<c>{ "statuses": [...], "when": "..." }</c>), and always writes the new shape.
/// </summary>
public sealed class RunAfterConditionJsonConverter : JsonConverter<RunAfterCondition>
{
    /// <inheritdoc/>
    public override RunAfterCondition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Legacy shape: ["Succeeded","Skipped"]
            using var doc = JsonDocument.ParseValue(ref reader);
            var statuses = ParseStatuses(doc.RootElement);
            return new RunAfterCondition { Statuses = statuses };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var condition = new RunAfterCondition();
            if (TryGetProperty(root, "statuses", out var statusesProp) && statusesProp.ValueKind == JsonValueKind.Array)
            {
                condition.Statuses = ParseStatuses(statusesProp);
            }
            if (TryGetProperty(root, "when", out var whenProp) && whenProp.ValueKind == JsonValueKind.String)
            {
                condition.When = whenProp.GetString();
            }
            return condition;
        }

        throw new JsonException($"Unexpected token '{reader.TokenType}' for RunAfterCondition.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RunAfterCondition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Statuses is not null)
        {
            writer.WritePropertyName("statuses");
            writer.WriteStartArray();
            foreach (var s in value.Statuses)
            {
                writer.WriteStringValue(s.ToString());
            }
            writer.WriteEndArray();
        }
        if (!string.IsNullOrEmpty(value.When))
        {
            writer.WriteString("when", value.When);
        }
        writer.WriteEndObject();
    }

    private static StepStatus[] ParseStatuses(JsonElement array)
    {
        var result = new StepStatus[array.GetArrayLength()];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Enum.TryParse<StepStatus>(item.GetString(), ignoreCase: true, out var s))
            {
                result[i++] = s;
            }
            else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var num) && Enum.IsDefined(typeof(StepStatus), num))
            {
                result[i++] = (StepStatus)num;
            }
            else
            {
                throw new JsonException($"Cannot deserialise '{item.GetRawText()}' as StepStatus.");
            }
        }
        return i == result.Length ? result : result[..i];
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
