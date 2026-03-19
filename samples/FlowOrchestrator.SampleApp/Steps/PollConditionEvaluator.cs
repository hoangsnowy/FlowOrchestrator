using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;

namespace FlowOrchestrator.SampleApp.Steps;

internal static class PollConditionEvaluator
{
    public static bool IsMatched(JsonElement payload, IDictionary<string, object?> inputs)
    {
        var conditionPath = inputs.TryGetString("pollConditionPath", out var path) ? path : null;
        if (!TryResolvePath(payload, conditionPath, out var target))
        {
            return false;
        }

        if (!inputs.TryGetValue("pollConditionEquals", out var expected))
        {
            return HasData(target);
        }

        return string.Equals(Normalize(target), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolvePath(JsonElement payload, string? path, out JsonElement target)
    {
        target = payload;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var objectValue))
            {
                target = objectValue;
                continue;
            }

            if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index >= 0 && index < target.GetArrayLength())
                {
                    target = target[index];
                    continue;
                }
            }

            return false;
        }

        return true;
    }

    private static bool HasData(JsonElement target) => target.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(target.GetString()),
        JsonValueKind.Array => target.GetArrayLength() > 0,
        JsonValueKind.Object => target.EnumerateObject().MoveNext(),
        _ => true
    };

    private static string Normalize(object? value) => value switch
    {
        null => string.Empty,
        JsonElement json => Normalize(json),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };
}
