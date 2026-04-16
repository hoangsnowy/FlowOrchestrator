using System.Text.Json;
using System.Text.Json.Serialization;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Calls an external HTTP API and returns the parsed JSON response.
/// Supports optional polling so the step can wait for an async remote operation to complete.
///
/// ── Advanced topic: PollableStepHandler pattern ─────────────────────────────
///
/// Extend PollableStepHandler{TInput} (where TInput : IPollableInput) instead of
/// IStepHandler{T} whenever a step must poll an external system:
///
///   • Implement only FetchAsync — the base class handles everything else:
///       – Incrementing PollAttempt and recording PollStartedAtUtc in the
///         persisted step inputs (survives app restarts between poll cycles).
///       – Returning StepStatus.Pending + DelayNextStep when the condition is
///         not yet met — Hangfire re-enqueues after pollIntervalSeconds.
///       – Returning StepStatus.Failed when pollTimeoutSeconds is exceeded.
///       – Evaluating pollConditionPath (dot-notation JSON path) against the
///         response and comparing to pollConditionEquals.
///
///   • IPollableInput marks the input class and adds the polling contract fields.
///     The __pollStartedAtUtc and __pollAttempt properties are internal state
///     serialised between poll attempts — do not rename them (used by base class).
///
/// Polling is opt-in per step invocation: set pollEnabled = true in the manifest
/// Inputs. Without it the step calls FetchAsync once and succeeds/fails immediately.
///
/// Used by:
///   OrderFulfillmentFlow → submit_to_wms  (polls until WMS job is accepted)
///   ShipmentTrackingFlow → check_shipment_status  (polls until carrier confirms)
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

    // Called on every poll attempt. Return the raw API response as a JsonElement.
    // PollableStepHandler evaluates the poll condition against this result automatically.
    protected override async ValueTask<(JsonElement Result, bool IsJson)> FetchAsync(
        IExecutionContext ctx, IFlowDefinition flow, IStepInstance<CallExternalApiStepInput> step)
    {
        var input = step.Inputs;
        var method = string.IsNullOrWhiteSpace(input.Method) ? "GET" : input.Method;
        var path   = string.IsNullOrWhiteSpace(input.Path)   ? "/"   : input.Path;

        _logger.LogInformation(
            "[CallExternalApi] RunId={RunId} Attempt={Attempt} {Method} {Path}",
            ctx.RunId, input.PollAttempt ?? 1, method, path);

        var client  = _httpClientFactory.CreateClient("ExternalApi");
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

        _logger.LogInformation("[CallExternalApi] RunId={RunId} Status={Status}", ctx.RunId, response.StatusCode);
        if (!parsedAsJson && !string.IsNullOrWhiteSpace(responseBody))
        {
            _logger.LogWarning(
                "[CallExternalApi] RunId={RunId} Response is not valid JSON — ContentType={ContentType} Preview={Preview}",
                ctx.RunId,
                response.Content.Headers.ContentType?.MediaType,
                responseBody.Length > 200 ? responseBody[..200] : responseBody);
        }

        return (result, parsedAsJson);
    }

    private static JsonElement ParseResponsePayload(string responseBody, string? mediaType, out bool parsedAsJson)
    {
        parsedAsJson = false;

        if (string.IsNullOrWhiteSpace(responseBody))
            return JsonSerializer.SerializeToElement<object?>(null);

        var looksLikeJson = IsLikelyJson(mediaType, responseBody);
        if ((looksLikeJson || !looksLikeJson) && TryParseJsonElement(responseBody, out var parsed))
        {
            parsedAsJson = true;
            return parsed;
        }

        return JsonSerializer.SerializeToElement(responseBody);
    }

    private static bool IsLikelyJson(string? mediaType, string responseBody)
    {
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return true;

        var first = responseBody.TrimStart().FirstOrDefault();
        return first is '{' or '[' or '"' or '-' or 't' or 'f' or 'n' || char.IsDigit(first);
    }

    private static bool TryParseJsonElement(string payload, out JsonElement element)
    {
        try   { element = JsonSerializer.Deserialize<JsonElement>(payload); return true; }
        catch (JsonException) { element = default; return false; }
    }
}

/// <summary>
/// Input for CallExternalApiStep. Implements IPollableInput so the base class can
/// manage poll state between Hangfire job executions.
/// </summary>
public sealed class CallExternalApiStepInput : IPollableInput
{
    public string Method { get; set; } = "GET";
    public string Path   { get; set; } = "/";
    public object? Body  { get; set; }

    // ── Polling contract (IPollableInput) ───────────────────────────────────
    // Configure these via the manifest Inputs dictionary.
    public bool   PollEnabled          { get; set; }
    public int    PollIntervalSeconds  { get; set; } = 10;
    public int    PollTimeoutSeconds   { get; set; } = 300;
    public int    PollMinAttempts      { get; set; } = 1;
    public string? PollConditionPath   { get; set; }
    public object? PollConditionEquals { get; set; }

    // ── Internal poll state — persisted to SQL between attempts ─────────────
    // The base class reads and writes these; do not rename (JsonPropertyName is load-bearing).
    [JsonPropertyName("__pollStartedAtUtc")]
    public string? PollStartedAtUtc { get; set; }

    [JsonPropertyName("__pollAttempt")]
    public int? PollAttempt { get; set; }
}
