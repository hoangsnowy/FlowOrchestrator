using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Calls an external HTTP API and returns the parsed JSON response.
/// Inputs: method (GET/POST/...), path (string), body (optional)
/// Poll inputs (optional): pollEnabled, pollIntervalSeconds, pollTimeoutSeconds, pollConditionPath, pollConditionEquals, pollMinAttempts
/// Output: deserialized JSON response
/// </summary>
public sealed class CallExternalApiStep : IStepHandler
{
    private const string PollStartedAtInputKey = "__pollStartedAtUtc";
    private const string PollAttemptInputKey = "__pollAttempt";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallExternalApiStep> _logger;

    public CallExternalApiStep(IHttpClientFactory httpClientFactory, ILogger<CallExternalApiStep> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async ValueTask<object?> ExecuteAsync(IExecutionContext ctx, IFlowDefinition flow, IStepInstance step)
    {
        var method = step.Inputs.TryGetValue("method", out var m) ? m?.ToString() ?? "GET" : "GET";
        var path = step.Inputs.TryGetValue("path", out var p) ? p?.ToString() ?? "/" : "/";

        _logger.LogInformation("[CallExternalApi] RunId={RunId} {Method} {Path}", ctx.RunId, method, path);

        var client = _httpClientFactory.CreateClient("ExternalApi");
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (step.Inputs.TryGetValue("body", out var body) && body is not null)
        {
            var json = body is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

        _logger.LogInformation("[CallExternalApi] RunId={RunId} Response status={Status}", ctx.RunId, response.StatusCode);

        if (!GetBoolInput(step.Inputs, "pollEnabled", defaultValue: false))
        {
            return result;
        }

        var intervalSeconds = Math.Max(1, GetIntInput(step.Inputs, "pollIntervalSeconds", defaultValue: 10));
        var timeoutSeconds = Math.Max(intervalSeconds, GetIntInput(step.Inputs, "pollTimeoutSeconds", defaultValue: 300));
        var minAttempts = Math.Max(1, GetIntInput(step.Inputs, "pollMinAttempts", defaultValue: 1));
        var pollStartedAt = GetPollStartedAt(step);
        var currentAttempt = IncrementPollAttempt(step);

        if (currentAttempt >= minAttempts && IsPollConditionMatched(result, step.Inputs))
        {
            ResetPollState(step);
            _logger.LogInformation(
                "[CallExternalApi] RunId={RunId} Polling condition matched at attempt {Attempt}. Continue flow.",
                ctx.RunId,
                currentAttempt);
            return result;
        }

        var elapsed = DateTimeOffset.UtcNow - pollStartedAt;
        if (elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
        {
            ResetPollState(step);
            return new StepResult
            {
                Key = step.Key,
                Status = StepStatus.Failed,
                FailedReason = $"Polling timed out after {timeoutSeconds} seconds.",
                Result = result
            };
        }

        _logger.LogInformation(
            "[CallExternalApi] RunId={RunId} Polling condition not met at attempt {Attempt}/{MinAttempts}. Retry in {Delay}s.",
            ctx.RunId,
            currentAttempt,
            minAttempts,
            intervalSeconds);

        return new StepResult
        {
            Key = step.Key,
            Status = StepStatus.Pending,
            DelayNextStep = TimeSpan.FromSeconds(intervalSeconds),
            Result = result
        };
    }

    private static DateTimeOffset GetPollStartedAt(IStepInstance step)
    {
        if (step.Inputs.TryGetValue(PollStartedAtInputKey, out var current)
            && TryParseDateTimeOffset(current, out var parsed))
        {
            return parsed;
        }

        var now = DateTimeOffset.UtcNow;
        step.Inputs[PollStartedAtInputKey] = now.ToString("O", CultureInfo.InvariantCulture);
        return now;
    }

    private static int IncrementPollAttempt(IStepInstance step)
    {
        if (step.Inputs.TryGetValue(PollAttemptInputKey, out var current))
        {
            var parsed = current switch
            {
                int i => i,
                long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                string s when int.TryParse(s, out var value) => value,
                JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var value) => value,
                JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var value) => value,
                _ => 0
            };

            var next = parsed + 1;
            step.Inputs[PollAttemptInputKey] = next;
            return next;
        }

        step.Inputs[PollAttemptInputKey] = 1;
        return 1;
    }

    private static void ResetPollState(IStepInstance step)
    {
        step.Inputs.Remove(PollStartedAtInputKey);
        step.Inputs.Remove(PollAttemptInputKey);
    }

    private static bool IsPollConditionMatched(JsonElement payload, IDictionary<string, object?> inputs)
    {
        var conditionPath = GetStringInput(inputs, "pollConditionPath");
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

    private static int GetIntInput(IDictionary<string, object?> inputs, string key, int defaultValue)
    {
        if (!inputs.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int i => i,
            long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var parsedInt) => parsedInt,
            JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), out var parsedString) => parsedString,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static bool GetBoolInput(IDictionary<string, object?> inputs, string key, bool defaultValue)
    {
        if (!inputs.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } je when bool.TryParse(je.GetString(), out var parsedBool) => parsedBool,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static string? GetStringInput(IDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => value.ToString()
        };
    }

    private static bool TryParseDateTimeOffset(object? value, out DateTimeOffset dateTimeOffset)
    {
        var text = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out dateTimeOffset);
    }
}
