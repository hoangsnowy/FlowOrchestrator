using System.Globalization;
using System.Text.Json;

namespace FlowOrchestrator.Core.Abstractions;

/// <summary>
/// Extension methods for extracting typed values from step or trigger input dictionaries.
/// Handles both strongly-typed CLR values and <see cref="JsonElement"/> representations,
/// which occur when inputs are deserialized from JSON manifests.
/// </summary>
public static class MetadataInputExtensions
{
    /// <summary>
    /// Tries to read a string value from <paramref name="inputs"/> at <paramref name="key"/>,
    /// normalising numeric and boolean <see cref="JsonElement"/> variants to their string forms.
    /// </summary>
    /// <param name="inputs">The input dictionary to read from.</param>
    /// <param name="key">The input key to look up.</param>
    /// <param name="value">The resolved string value, or <see cref="string.Empty"/> if not found.</param>
    /// <returns><see langword="true"/> if a non-whitespace value was found; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Tries to read a 32-bit integer from <paramref name="inputs"/> at <paramref name="key"/>,
    /// accepting <c>int</c>, <c>long</c> (within range), and numeric or string <see cref="JsonElement"/> values.
    /// </summary>
    /// <param name="inputs">The input dictionary to read from.</param>
    /// <param name="key">The input key to look up.</param>
    /// <param name="value">The parsed integer, or <c>0</c> if not found or conversion fails.</param>
    /// <returns><see langword="true"/> if a valid integer was found; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Tries to read a boolean from <paramref name="inputs"/> at <paramref name="key"/>,
    /// accepting native <c>bool</c>, JSON true/false literals, and parseable strings.
    /// </summary>
    /// <param name="inputs">The input dictionary to read from.</param>
    /// <param name="key">The input key to look up.</param>
    /// <param name="value">The parsed boolean, or <see langword="false"/> if not found.</param>
    /// <returns><see langword="true"/> if a valid boolean was found; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Tries to read a <see cref="DateTimeOffset"/> from <paramref name="inputs"/> at <paramref name="key"/>.
    /// Accepts ISO 8601 strings and <see cref="JsonElement"/> string values; parses with round-trip kind.
    /// </summary>
    /// <param name="inputs">The input dictionary to read from.</param>
    /// <param name="key">The input key to look up.</param>
    /// <param name="value">The parsed <see cref="DateTimeOffset"/>, or <c>default</c> if not found.</param>
    /// <returns><see langword="true"/> if a valid date/time was found; otherwise <see langword="false"/>.</returns>
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
