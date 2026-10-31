using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>
/// Masks secret-looking values in flow manifests and run payloads before they are shown to a user who may
/// only view.
/// </summary>
/// <remarks>
/// <para>Webhook receive endpoints are protected only by the trigger's <c>webhookSecret</c> / HMAC key, and the
/// manifest carries those values verbatim; step inputs persist the step's configured inputs (API keys,
/// tokens) as written in the manifest. Without masking, a <c>flow-viewer</c> could copy them from the flow
/// catalog or a run and act outside their role. Operators (and unprotected dashboards) keep seeing payloads
/// as stored.</para>
/// <para>Masking is by property name, anywhere in the JSON: any value under a name containing one of
/// <see cref="SecretNameFragments"/> is replaced, whatever its JSON type. It is a safety net for viewers,
/// not a substitute for keeping secrets out of manifests (prefer configuration references).</para>
/// </remarks>
internal static class ManifestSecretRedactor
{
    /// <summary>Value written in place of a masked secret.</summary>
    public const string Mask = "***redacted***";

    // Matched case-insensitively against property names anywhere in the JSON.
    private static readonly string[] SecretNameFragments =
    [
        "secret", "password", "passwd", "apikey", "api_key", "token", "hmackey", "privatekey",
        "connectionstring", "credential", "authorization", "bearer", "cookie", "signature",
    ];

    // /api/flows is polled by the overview; viewers would otherwise re-parse every manifest on every poll.
    // Keyed by flow id + manifest content; bounded by clearing once it outgrows any realistic flow count.
    private static readonly ConcurrentDictionary<(Guid Id, int Hash, int Length), string?> ManifestCache = new();
    private const int ManifestCacheLimit = 2_048;

    /// <summary>Returns <paramref name="flows"/> unchanged for operators, or copies with masked manifests for viewers.</summary>
    /// <param name="flows">Flow records from the store.</param>
    /// <param name="canSeeSecrets"><see langword="true"/> for operators and unprotected dashboards.</param>
    /// <returns>The records to serialise.</returns>
    public static IEnumerable<FlowDefinitionRecord> ForViewer(IEnumerable<FlowDefinitionRecord> flows, bool canSeeSecrets)
        => canSeeSecrets ? flows : flows.Select(Redacted);

    /// <summary>Returns a copy of <paramref name="flow"/> whose manifest has every secret-looking value masked.</summary>
    /// <param name="flow">The stored record; not modified.</param>
    /// <returns>A copy safe to show to a viewer.</returns>
    public static FlowDefinitionRecord Redacted(FlowDefinitionRecord flow) => new()
    {
        Id = flow.Id,
        Name = flow.Name,
        Version = flow.Version,
        ManifestJson = RedactManifestCached(flow.Id, flow.ManifestJson),
        IsEnabled = flow.IsEnabled,
        CreatedAt = flow.CreatedAt,
        UpdatedAt = flow.UpdatedAt,
    };

    /// <summary>
    /// Returns a copy of <paramref name="run"/> whose trigger body and step / attempt inputs and outputs have
    /// every secret-looking value masked. The stored record is not modified.
    /// </summary>
    /// <param name="run">The run as read from the store.</param>
    /// <returns>A copy safe to show to a viewer.</returns>
    public static FlowRunRecord Redacted(FlowRunRecord run) => new()
    {
        Id = run.Id,
        FlowId = run.FlowId,
        FlowName = run.FlowName,
        Status = run.Status,
        TriggerKey = run.TriggerKey,
        TriggerDataJson = RedactJson(run.TriggerDataJson),
        TriggerHeaders = run.TriggerHeaders,
        BackgroundJobId = run.BackgroundJobId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        SourceRunId = run.SourceRunId,
        Steps = run.Steps?.Select(Redacted).ToList(),
    };

    /// <summary>Returns a copy of <paramref name="step"/> with its inputs, outputs and attempts masked.</summary>
    /// <param name="step">The step as read from the store.</param>
    /// <returns>A copy safe to show to a viewer.</returns>
    public static FlowStepRecord Redacted(FlowStepRecord step) => new()
    {
        RunId = step.RunId,
        StepKey = step.StepKey,
        StepType = step.StepType,
        Status = step.Status,
        InputJson = RedactJson(step.InputJson),
        OutputJson = RedactJson(step.OutputJson),
        ErrorMessage = step.ErrorMessage,
        JobId = step.JobId,
        StartedAt = step.StartedAt,
        CompletedAt = step.CompletedAt,
        AttemptCount = step.AttemptCount,
        Attempts = step.Attempts?.Select(a => new FlowStepAttemptRecord
        {
            RunId = a.RunId,
            StepKey = a.StepKey,
            Attempt = a.Attempt,
            StepType = a.StepType,
            Status = a.Status,
            InputJson = RedactJson(a.InputJson),
            OutputJson = RedactJson(a.OutputJson),
            ErrorMessage = a.ErrorMessage,
            JobId = a.JobId,
            StartedAt = a.StartedAt,
            CompletedAt = a.CompletedAt,
            EvaluationTraceJson = a.EvaluationTraceJson,
        }).ToList(),
        EvaluationTraceJson = step.EvaluationTraceJson,
    };

    private static string? RedactManifestCached(Guid id, string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return manifestJson;

        var key = (id, manifestJson.GetHashCode(), manifestJson.Length);
        if (ManifestCache.TryGetValue(key, out var cached))
            return cached;

        if (ManifestCache.Count >= ManifestCacheLimit)
            ManifestCache.Clear();
        var redacted = RedactJson(manifestJson);
        ManifestCache[key] = redacted;
        return redacted;
    }

    /// <summary>Masks every value whose property name looks like a secret.</summary>
    /// <param name="json">JSON text; <see langword="null"/> or blank is returned as is.</param>
    /// <returns>The masked JSON, or <see langword="null"/> when <paramref name="json"/> is not valid JSON.</returns>
    /// <remarks>Invalid JSON is dropped rather than returned: it cannot be inspected, so it cannot be shown safely.</remarks>
    internal static string? RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return json;

        try
        {
            var root = JsonNode.Parse(json);
            Redact(root);
            return root?.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // JsonException: not JSON. ArgumentException / InvalidOperationException: duplicate property
            // names, which JsonObject rejects when it is materialised.
            return null;
        }
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (obj[name] is not null && IsSecretName(name))
                        obj[name] = Mask;
                    else
                        Redact(obj[name]);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    Redact(item);
                break;
        }
    }

    private static bool IsSecretName(string name)
    {
        foreach (var fragment in SecretNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
