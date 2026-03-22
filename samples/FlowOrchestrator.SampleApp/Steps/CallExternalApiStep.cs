using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Calls an external HTTP API and returns the parsed JSON response.
/// Inputs: method (GET/POST/...), path (string), body (optional)
/// Poll inputs (optional): pollEnabled, pollIntervalSeconds, pollTimeoutSeconds, pollConditionPath, pollConditionEquals, pollMinAttempts
/// Output: deserialized JSON response
/// </summary>
public sealed class CallExternalApiStep : PollableStepHandler<CallExternalApiStepInput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallExternalApiStep> _logger;

    public CallExternalApiStep(IHttpClientFactory httpClientFactory, ILogger<CallExternalApiStep> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CallExternalApiStepInput> step)
    {
        var input = step.Inputs;
        var method = string.IsNullOrWhiteSpace(input.Method) ? "GET" : input.Method;
        var path = string.IsNullOrWhiteSpace(input.Path) ? "/" : input.Path;

        _logger.LogInformation("[CallExternalApi] RunId={RunId} {Method} {Path}", ctx.RunId, method, path);

        var client = _httpClientFactory.CreateClient("ExternalApi");
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (input.Body is not null)
        {
            var json = input.Body is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(input.Body);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var result = ParseResponsePayload(responseBody, response.Content.Headers.ContentType?.MediaType, out var parsedAsJson);

        _logger.LogInformation("[CallExternalApi] RunId={RunId} Response status={Status}", ctx.RunId, response.StatusCode);
        if (!parsedAsJson && !string.IsNullOrWhiteSpace(responseBody))
        {
            _logger.LogWarning(
                "[CallExternalApi] RunId={RunId} Response body is not valid JSON. ContentType={ContentType}. Body preview={Preview}",
                ctx.RunId,
                response.Content.Headers.ContentType?.MediaType,
                GetBodyPreview(responseBody));
        }

        return (result, parsedAsJson);
    }

    private static JsonElement ParseResponsePayload(string responseBody, string? mediaType, out bool parsedAsJson)
    {
        parsedAsJson = false;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        var looksLikeJson = IsLikelyJson(mediaType, responseBody);
        if (looksLikeJson && TryParseJsonElement(responseBody, out var parsed))
        {
            parsedAsJson = true;
            return parsed;
        }

        if (!looksLikeJson && TryParseJsonElement(responseBody, out parsed))
        {
            parsedAsJson = true;
            return parsed;
        }

        return JsonSerializer.SerializeToElement(responseBody);
    }

    private static bool IsLikelyJson(string? mediaType, string responseBody)
    {
        if (!string.IsNullOrWhiteSpace(mediaType)
            && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = responseBody.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var first = trimmed[0];
        return first is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || char.IsDigit(first);
    }

    private static bool TryParseJsonElement(string payload, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(payload);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static string GetBodyPreview(string body)
    {
        var maxLength = 200;
        if (body.Length <= maxLength)
        {
            return body;
        }

        return body[..maxLength];
    }
}

public sealed class CallExternalApiStepInput : IPollableInput
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public object? Body { get; set; }

    public bool PollEnabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 10;
    public int PollTimeoutSeconds { get; set; } = 300;
    public int PollMinAttempts { get; set; } = 1;

    public string? PollConditionPath { get; set; }
    public object? PollConditionEquals { get; set; }

    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}
