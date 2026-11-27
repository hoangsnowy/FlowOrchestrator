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
/// <para>A name is normalised before it is matched: the separators in <see cref="NameSeparators"/> are
/// dropped and the comparison ignores case, so <c>X-Api-Key</c>, <c>api_key</c>, <c>API.KEY</c> and
/// <c>apiKey</c> are the same name. The fragments are deliberately broad. Masking a value that was not a
/// secret costs a viewer some detail; showing one that was is a credential leak, so a doubtful name is
/// masked (<c>tokenCount</c>, <c>signatureVersion</c> and <c>keyPassword</c> are masked too). The one fragment
/// considered and left out is a bare <c>sas</c>, which would also hit ordinary words such as
/// <c>disaster</c>; SAS credentials are caught through <c>saskey</c>, <c>sasurl</c>, <c>sasuri</c>,
/// <c>sharedaccess</c> and <c>token</c> instead.</para>
/// </remarks>
internal static class ManifestSecretRedactor
{
    /// <summary>Value written in place of a masked secret.</summary>
    public const string Mask = "***redacted***";

    // Matched case-insensitively against the NORMALISED name (separators dropped), anywhere in the name.
    // Keep every entry free of separators: a fragment containing one could never match.
    private static readonly string[] SecretNameFragments =
    [
        // Generic secrets and passwords.
        "secret", "password", "passwd", "pwd", "passphrase", "credential", "connectionstring",
        // Tokens and HTTP authentication material.
        "token", "bearer", "authorization", "cookie", "sessionid", "signature", "jwt",
        // Keys: API keys and the vendor-specific header / setting names for them.
        "apikey", "privatekey", "hmackey", "signingkey", "accesskey", "accountkey", "masterkey",
        "encryptionkey", "subscriptionkey", "functionkey", "functionskey", "webhookkey", "authkey",
        // Shared access signatures (Azure Storage / Service Bus).
        "sharedaccess", "saskey", "sasurl", "sasuri",
    ];

    private static readonly System.Buffers.SearchValues<string> SecretNameSearch =
        System.Buffers.SearchValues.Create(SecretNameFragments, StringComparison.OrdinalIgnoreCase);

    // Dropped from a name before matching. Header names use '-', JSON / env-style names '_', configuration
    // paths '.', and display-style names spaces.
    private static readonly System.Buffers.SearchValues<char> NameSeparators = System.Buffers.SearchValues.Create("-_. ");

    // Names up to this length are normalised on the stack; longer ones (rare) rent a pooled buffer.
    private const int StackNameLimit = 256;

    // /api/flows is polled by the overview; viewers would otherwise re-parse every manifest on every poll.
    // Keyed by flow id + manifest content; bounded by clearing once it outgrows any realistic flow count.
    private static readonly ConcurrentDictionary<(Guid Id, int Length), (string Source, string? Redacted)> ManifestCache = new();
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
        TriggerHeaders = MaskHeaders(run.TriggerHeaders),
        BackgroundJobId = run.BackgroundJobId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        SourceRunId = run.SourceRunId,
        Steps = run.Steps?.Select(Redacted).ToList(),
    };

    /// <summary>
    /// Returns a run-list row for a viewer: the trigger body is dropped (the list never shows it; run detail
    /// shows it masked), so a polled list costs no JSON parsing.
    /// </summary>
    /// <param name="run">The row as read from the store.</param>
    /// <returns>A copy without the trigger body.</returns>
    public static FlowRunRecord RedactedListRow(FlowRunRecord run) => new()
    {
        Id = run.Id,
        FlowId = run.FlowId,
        FlowName = run.FlowName,
        Status = run.Status,
        TriggerKey = run.TriggerKey,
        TriggerDataJson = null,
        TriggerHeaders = MaskHeaders(run.TriggerHeaders),
        BackgroundJobId = run.BackgroundJobId,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        SourceRunId = run.SourceRunId,
    };

    /// <summary>Masks header values whose name looks secret (e.g. <c>X-Api-Key</c>, <c>X-Auth-Token</c>).</summary>
    /// <param name="headers">Captured trigger headers; <c>Authorization</c> / <c>Cookie</c> / <c>X-Webhook-Key</c> are already dropped at capture.</param>
    /// <returns>A masked copy, or <see langword="null"/>.</returns>
    internal static IReadOnlyDictionary<string, string>? MaskHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return null;
        var masked = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            masked[name] = IsSecretName(name) ? Mask : value;
        return masked;
    }

    /// <summary>Returns a webhook delivery log row safe for a viewer: no body excerpt, secret-looking headers masked.</summary>
    /// <param name="record">The stored row.</param>
    /// <returns>A masked copy.</returns>
    /// <remarks>The body excerpt is cut at a byte limit, so it is usually not valid JSON and cannot be masked by name; viewers get none.</remarks>
    public static WebhookRejectionRecord Redacted(WebhookRejectionRecord record)
        => record with { BodyTruncated = null, HeadersJson = RedactJson(record.HeadersJson) };

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
            EvaluationTraceJson = RedactEvaluationTrace(a.EvaluationTraceJson),
        }).ToList(),
        EvaluationTraceJson = RedactEvaluationTrace(step.EvaluationTraceJson),
    };

    private static string? RedactManifestCached(Guid id, string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return manifestJson;

        // (Id, Length) + an ordinal compare: no per-poll hash of the whole manifest, and the compare returns at
        // once when the store hands back the same string instance.
        var key = (id, manifestJson.Length);
        // The source is compared ordinally, so a hash collision costs a re-parse, never a wrong result.
        if (ManifestCache.TryGetValue(key, out var cached) && string.Equals(cached.Source, manifestJson, StringComparison.Ordinal))
            return cached.Redacted;

        if (ManifestCache.Count >= ManifestCacheLimit)
            ManifestCache.Clear();
        var redacted = RedactJson(manifestJson);
        ManifestCache[key] = (manifestJson, redacted);
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

        // Most payloads carry no secret-looking name: scanning the text is ~20x cheaper than parsing and
        // allocates nothing. A \u escape could spell a name the scan cannot see, so it forces the parse.
        if (!MayContainSecretName(json))
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

    /// <summary>
    /// Masks the resolved values of a skipped step's <c>When</c> trace when its expression reads a
    /// secret-looking name (for example <c>@triggerHeaders()['X-Api-Key']</c>).
    /// </summary>
    /// <param name="json">The stored trace (<c>{"expression","resolved","result"}</c>), or <see langword="null"/>.</param>
    /// <returns>The trace with <c>resolved</c> masked when needed; <see langword="null"/> when it is not valid JSON.</returns>
    /// <remarks>
    /// The trace's own property names are never secret-looking, so <see cref="RedactJson"/> would pass it
    /// through; but <c>resolved</c> is the expression with every operand replaced by its runtime value, i.e.
    /// it carries the very value the trigger body / headers mask. The expression stays visible: it comes from
    /// the manifest, which a viewer can already read.
    /// </remarks>
    internal static string? RedactEvaluationTrace(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !MayContainSecretName(json))
            return json;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject trace)
                return json;
            if (trace["expression"] is JsonValue expression
                && expression.TryGetValue<string>(out var text)
                && IsSecretName(text)
                && trace["resolved"] is not null)
            {
                trace["resolved"] = Mask;
            }
            return trace.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap text pre-check for <see cref="RedactJson"/>: <see langword="false"/> only when no property name in
    /// <paramref name="json"/> can be secret-looking, so the parse can be skipped.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns><see langword="true"/> when the normalised text contains a fragment, or the text contains a <c>\u</c> escape.</returns>
    /// <remarks>
    /// <para>Must never be stricter than <see cref="IsSecretName(ReadOnlySpan{char})"/>. Every property name appears
    /// verbatim in the text (escapes aside, which force the parse), so its normalised form is a substring of the
    /// normalised text: dropping the same separators from the whole text and searching it finds every name
    /// <see cref="IsSecretName(ReadOnlySpan{char})"/> would flag. A fragment that only appears across a value and a
    /// name merely costs a parse.</para>
    /// <para>Cost: one vectorised search of the raw text; a text that also contains a separator (almost every
    /// payload: GUIDs, dates, decimals) costs one more copy into a pooled buffer and a second search. Viewers
    /// only; operators never reach the redactor.</para>
    /// </remarks>
    internal static bool MayContainSecretName(string json)
    {
        var text = json.AsSpan();
        if (text.IndexOf(@"\u", StringComparison.Ordinal) >= 0)
            return true;
        if (text.IndexOfAny(SecretNameSearch) >= 0)
            return true;
        if (text.IndexOfAny(NameSeparators) < 0)
            return false;

        var rented = System.Buffers.ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var length = StripSeparators(text, rented);
            return rented.AsSpan(0, length).IndexOfAny(SecretNameSearch) >= 0;
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary><see langword="true"/> when a property or header name looks like it carries a secret.</summary>
    /// <param name="name">The property or header name, as written.</param>
    /// <returns><see langword="true"/> when the normalised name contains one of <see cref="SecretNameFragments"/>.</returns>
    internal static bool IsSecretName(string name) => IsSecretName(name.AsSpan());

    /// <summary><see langword="true"/> when a property or header name looks like it carries a secret.</summary>
    /// <param name="name">The property or header name, as written.</param>
    /// <returns><see langword="true"/> when the normalised name contains one of <see cref="SecretNameFragments"/>.</returns>
    /// <remarks>
    /// Runs once per property per viewer response: no allocation for names up to <see cref="StackNameLimit"/>
    /// characters, and no copy at all for a name without separators.
    /// </remarks>
    internal static bool IsSecretName(ReadOnlySpan<char> name)
    {
        if (name.IndexOfAny(NameSeparators) < 0)
            return name.IndexOfAny(SecretNameSearch) >= 0;

        char[]? rented = null;
        var buffer = name.Length <= StackNameLimit
            ? stackalloc char[StackNameLimit]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(name.Length));
        try
        {
            var length = StripSeparators(name, buffer);
            return buffer[..length].IndexOfAny(SecretNameSearch) >= 0;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> without its <see cref="NameSeparators"/>.</summary>
    /// <returns>The number of characters written.</returns>
    private static int StripSeparators(ReadOnlySpan<char> source, Span<char> destination)
    {
        var written = 0;
        while (true)
        {
            var next = source.IndexOfAny(NameSeparators);
            var chunk = next < 0 ? source : source[..next];
            chunk.CopyTo(destination[written..]);
            written += chunk.Length;
            if (next < 0)
                return written;
            source = source[(next + 1)..];
        }
    }
}
