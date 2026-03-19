using System.Globalization;
using System.Text.Json;

namespace FlowOrchestrator.Core.Abstractions;

public static class MetadataInputExtensions
{
    public static bool TryGetString(this IDictionary<string, object?> inputs, string key, out string value)
    {
        value = string.Empty;
        if (!inputs.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        var converted = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } json => json.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } => bool.TrueString,
            JsonElement { ValueKind: JsonValueKind.False } => bool.FalseString,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString()
        };

        if (string.IsNullOrWhiteSpace(converted))
        {
            return false;
        }

        value = converted;
        return true;
    }

    public static bool TryGetInt32(this IDictionary<string, object?> inputs, string key, out int value)
    {
        value = default;
        if (!inputs.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var parsedInt):
                value = parsedInt;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } json when int.TryParse(json.GetString(), out var parsedString):
                value = parsedString;
                return true;
            case string s when int.TryParse(s, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetBoolean(this IDictionary<string, object?> inputs, string key, out bool value)
    {
        value = default;
        if (!inputs.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                value = false;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } json when bool.TryParse(json.GetString(), out var parsedString):
                value = parsedString;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetDateTimeOffset(this IDictionary<string, object?> inputs, string key, out DateTimeOffset value)
    {
        value = default;
        if (!inputs.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        var text = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out value);
    }
}
