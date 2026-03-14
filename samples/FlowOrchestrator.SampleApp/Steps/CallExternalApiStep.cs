using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.SampleApp.Steps;

/// <summary>
/// Calls an external HTTP API and returns the parsed JSON response.
/// Inputs: method (GET/POST/...), path (string), body (optional)
/// Output: deserialized JSON response
/// </summary>
public sealed class CallExternalApiStep : IStepHandler
{
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

        return result;
    }
}
