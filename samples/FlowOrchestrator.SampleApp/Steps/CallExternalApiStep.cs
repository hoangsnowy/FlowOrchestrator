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
        var method = step.Inputs.TryGetString("method", out var methodInput) ? methodInput : "GET";
        var path = step.Inputs.TryGetString("path", out var pathInput) ? pathInput : "/";

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

        var pollEnabled = step.Inputs.TryGetBoolean("pollEnabled", out var enabled) && enabled;
        if (!pollEnabled)
        {
            return result;
        }

        var intervalSeconds = Math.Max(1, step.Inputs.TryGetInt32("pollIntervalSeconds", out var intervalInput) ? intervalInput : 10);
        var timeoutSeconds = Math.Max(intervalSeconds, step.Inputs.TryGetInt32("pollTimeoutSeconds", out var timeoutInput) ? timeoutInput : 300);
        var minAttempts = Math.Max(1, step.Inputs.TryGetInt32("pollMinAttempts", out var minAttemptsInput) ? minAttemptsInput : 1);
        var pollStartedAt = GetPollStartedAt(step);
        var currentAttempt = IncrementPollAttempt(step);

        if (currentAttempt >= minAttempts && PollConditionEvaluator.IsMatched(result, step.Inputs))
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
            
             = result
        };
    }

    private static DateTimeOffset GetPollStartedAt(IStepInstance step)
    {
        if (step.Inputs.TryGetDateTimeOffset(PollStartedAtInputKey, out var parsed))
        {
            return parsed;
        }

        var now = DateTimeOffset.UtcNow;
        step.Inputs[PollStartedAtInputKey] = now.ToString("O", CultureInfo.InvariantCulture);
        return now;
    }

    private static int IncrementPollAttempt(IStepInstance step)
    {
        var parsed = step.Inputs.TryGetInt32(PollAttemptInputKey, out var attempt) ? attempt : 0;
        var next = parsed + 1;
        step.Inputs[PollAttemptInputKey] = next;

        return next;
    }

    private static void ResetPollState(IStepInstance step)
    {
        step.Inputs.Remove(PollStartedAtInputKey);
        step.Inputs.Remove(PollAttemptInputKey);
    }
}
