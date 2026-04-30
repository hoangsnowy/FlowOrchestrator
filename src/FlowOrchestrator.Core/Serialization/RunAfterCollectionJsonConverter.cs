using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.Core.Serialization;

/// <summary>
/// Reads and writes a <see cref="RunAfterCollection"/> as a JSON object whose keys are
/// predecessor step keys and whose values are <see cref="RunAfterCondition"/> entries.
/// Each value may be the legacy array shape (<c>["Succeeded"]</c>) or the new object shape
/// (<c>{ "statuses": [...], "when": "..." }</c>); see <see cref="RunAfterConditionJsonConverter"/>.
/// </summary>
public sealed class RunAfterCollectionJsonConverter : JsonConverter<RunAfterCollection>
{
    /// <inheritdoc/>
    public override RunAfterCollection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Unexpected token '{reader.TokenType}' for RunAfterCollection.");
        }

        var collection = new RunAfterCollection();
        var conditionConverter = (JsonConverter<RunAfterCondition>)options.GetConverter(typeof(RunAfterCondition));

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return collection;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token '{reader.TokenType}' inside RunAfterCollection.");
            }

            var key = reader.GetString() ?? string.Empty;
            reader.Read();
            var condition = conditionConverter.Read(ref reader, typeof(RunAfterCondition), options);
            if (condition is not null)
            {
                collection[key] = condition;
            }
        }

        throw new JsonException("Unterminated RunAfterCollection JSON object.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RunAfterCollection value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        var conditionConverter = (JsonConverter<RunAfterCondition>)options.GetConverter(typeof(RunAfterCondition));
        foreach (var (key, condition) in value)
        {
            writer.WritePropertyName(key);
            conditionConverter.Write(writer, condition, options);
        }
        writer.WriteEndObject();
    }
}
