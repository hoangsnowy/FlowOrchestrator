using FlowOrchestrator.Core.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlowOrchestrator.Core.Serialization;

/// <summary>
/// Newtonsoft.Json converter for <see cref="RunAfterCondition"/>. Exists so that
/// Hangfire's job-argument serializer (which uses Newtonsoft) can read both legacy
/// payloads (array of <see cref="StepStatus"/> values, written before Plan 05) and
/// the new object shape (<c>{ "statuses": [...], "when": "..." }</c>).
/// </summary>
/// <remarks>
/// Outside Hangfire, all manifest serialisation goes through System.Text.Json via
/// <see cref="RunAfterConditionJsonConverter"/>. This converter only kicks in for
/// Hangfire payloads.
/// </remarks>
public sealed class RunAfterConditionNewtonsoftConverter : JsonConverter<RunAfterCondition>
{
    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override RunAfterCondition? ReadJson(JsonReader reader, Type objectType, RunAfterCondition? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var token = JToken.Load(reader);

        // Legacy payload: ["Succeeded", "Skipped"] — back-compat for jobs enqueued before Plan 05.
        if (token.Type == JTokenType.Array)
        {
            var statuses = new List<StepStatus>();
            foreach (var item in (JArray)token)
            {
                statuses.Add(ParseStatus(item));
            }
            return new RunAfterCondition { Statuses = statuses.ToArray() };
        }

        if (token.Type == JTokenType.Object)
        {
            var obj = (JObject)token;
            var condition = new RunAfterCondition();

            var statusesProp = obj.Property("statuses", StringComparison.OrdinalIgnoreCase) ?? obj.Property("Statuses", StringComparison.OrdinalIgnoreCase);
            if (statusesProp?.Value is JArray arr)
            {
                var list = new List<StepStatus>(arr.Count);
                foreach (var item in arr)
                {
                    list.Add(ParseStatus(item));
                }
                condition.Statuses = list.ToArray();
            }

            var whenProp = obj.Property("when", StringComparison.OrdinalIgnoreCase) ?? obj.Property("When", StringComparison.OrdinalIgnoreCase);
            if (whenProp?.Value.Type == JTokenType.String)
            {
                condition.When = whenProp.Value.Value<string>();
            }

            return condition;
        }

        throw new JsonSerializationException($"Unexpected token '{token.Type}' for RunAfterCondition.");
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, RunAfterCondition? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        if (value.Statuses is not null)
        {
            writer.WritePropertyName("statuses");
            writer.WriteStartArray();
            foreach (var s in value.Statuses)
            {
                writer.WriteValue(s.ToString());
            }
            writer.WriteEndArray();
        }
        if (!string.IsNullOrEmpty(value.When))
        {
            writer.WritePropertyName("when");
            writer.WriteValue(value.When);
        }
        writer.WriteEndObject();
    }

    private static StepStatus ParseStatus(JToken token)
    {
        if (token.Type == JTokenType.String)
        {
            var s = token.Value<string>();
            if (Enum.TryParse<StepStatus>(s, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }
        else if (token.Type == JTokenType.Integer)
        {
            var i = token.Value<int>();
            if (Enum.IsDefined(typeof(StepStatus), i))
            {
                return (StepStatus)i;
            }
        }

        throw new JsonSerializationException($"Cannot deserialise '{token}' as StepStatus.");
    }
}
