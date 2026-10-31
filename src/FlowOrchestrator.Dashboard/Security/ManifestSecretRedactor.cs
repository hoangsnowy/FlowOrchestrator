using System.Text.Json;
using System.Text.Json.Nodes;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Security;

/// <summary>
/// Masks secret-looking values in a flow manifest before it is shown to a user who may only view.
/// </summary>
/// <remarks>
/// Webhook receive endpoints are protected only by the trigger's <c>webhookSecret</c> / HMAC key, and the
/// manifest carries those values verbatim. Without masking, a <c>flow-viewer</c> could copy the secret from
/// the flow catalog and start runs through the webhook endpoint, which the viewer / operator split promises
/// they cannot do. Operators (and unprotected dashboards) keep seeing the manifest as stored.
/// </remarks>
internal static class ManifestSecretRedactor
{
    /// <summary>Value written in place of a masked secret.</summary>
    public const string Mask = "***redacted***";

    // Matched case-insensitively against property names anywhere in the manifest.
    private static readonly string[] SecretNameFragments =
        ["secret", "password", "passwd", "apikey", "api_key", "token", "hmackey", "privatekey", "connectionstring", "credential"];

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
        ManifestJson = RedactJson(flow.ManifestJson),
        IsEnabled = flow.IsEnabled,
        CreatedAt = flow.CreatedAt,
        UpdatedAt = flow.UpdatedAt,
    };

    /// <summary>Masks every string / number value whose property name looks like a secret.</summary>
    /// <param name="manifestJson">Manifest JSON; <see langword="null"/> or invalid JSON yields <see langword="null"/>.</param>
    /// <returns>The masked JSON.</returns>
    /// <remarks>Invalid JSON is dropped rather than returned: it cannot be inspected, so it cannot be shown safely.</remarks>
    internal static string? RedactJson(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return manifestJson;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return null;
        }

        Redact(root);
        return root?.ToJsonString();
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (IsSecretName(name) && value is JsonValue)
                        obj[name] = Mask;
                    else
                        Redact(value);
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
