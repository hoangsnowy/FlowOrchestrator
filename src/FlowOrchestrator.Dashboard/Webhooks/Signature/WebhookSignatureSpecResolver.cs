namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Resolves the active <see cref="WebhookSignatureSpec"/> for a webhook trigger
/// by inspecting the flow manifest <c>Inputs</c> dictionary. Order of precedence:
/// (1) named custom scheme registered through <c>WebhookSecurityOptionsBuilder.RegisterScheme</c>,
/// (2) built-in <see cref="WebhookSignatureScheme"/>, (3) full custom spec built
/// from individual manifest fields, (4) <see langword="null"/> when no signing
/// is requested.
/// </summary>
public static class WebhookSignatureSpecResolver
{
    /// <summary>
    /// Returns the spec to use for the trigger or <see langword="null"/> when
    /// the flow has not opted into signature verification.
    /// </summary>
    /// <param name="inputs">Flow manifest <c>Inputs</c> dictionary for the webhook trigger.</param>
    /// <param name="customSchemes">Named custom schemes registered on <see cref="Webhooks.WebhookSecurityOptions"/>.</param>
    public static WebhookSignatureSpec? Resolve(
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, WebhookSignatureSpec>? customSchemes)
    {
        if (!inputs.TryGetValue("webhookSignatureScheme", out var schemeObj) || schemeObj is null)
            return null;

        var schemeName = schemeObj.ToString();
        if (string.IsNullOrWhiteSpace(schemeName)) return null;

        // Custom registry overlay first.
        if (customSchemes is not null && customSchemes.TryGetValue(schemeName, out var custom))
            return custom;

        // Built-in enum value next.
        if (Enum.TryParse<WebhookSignatureScheme>(schemeName, ignoreCase: true, out var builtIn)
            && builtIn != WebhookSignatureScheme.None
            && builtIn != WebhookSignatureScheme.Custom)
        {
            return PartnerSchemeRegistry.TryGet(builtIn);
        }

        // Custom shape — assemble a spec from individual fields.
        if (string.Equals(schemeName, nameof(WebhookSignatureScheme.Custom), StringComparison.OrdinalIgnoreCase))
            return BuildFromCustomFields(inputs);

        return null;
    }

    private static WebhookSignatureSpec BuildFromCustomFields(IReadOnlyDictionary<string, object?> inputs)
    {
        return new WebhookSignatureSpec
        {
            HeaderName = GetString(inputs, "webhookSignatureHeader") ?? string.Empty,
            Algorithm = ParseEnum(GetString(inputs, "webhookSignatureAlgorithm"), HmacAlgorithm.Sha256),
            Encoding = ParseEnum(GetString(inputs, "webhookSignatureEncoding"), SignatureEncoding.HexLower),
            Prefix = GetString(inputs, "webhookSignaturePrefix"),
            MultiValueDelimiter = GetString(inputs, "webhookSignatureMultiValueDelimiter"),
            KeyValueSeparator = GetString(inputs, "webhookSignatureKeyValueSeparator"),
            SignatureValueKey = GetString(inputs, "webhookSignatureValueKey"),
            TimestampValueKey = GetString(inputs, "webhookTimestampValueKey"),
            TimestampHeaderName = GetString(inputs, "webhookTimestampHeader"),
            AcceptedVersions = GetStringList(inputs, "webhookAcceptedVersions"),
            SignedPayloadStrategy = ParseEnum(GetString(inputs, "webhookSignedPayloadStrategy"), SignedPayloadStrategy.RawBody),
            SignedPayloadDelimiter = GetString(inputs, "webhookSignedPayloadDelimiter"),
            SignedPayloadVersion = GetString(inputs, "webhookSignedPayloadVersion"),
            HeaderValuePrefix = GetString(inputs, "webhookHeaderValuePrefix"),
            CustomStrategyName = GetString(inputs, "webhookCustomStrategyName"),
            RequireTimestamp = GetBool(inputs, "webhookRequireTimestamp"),
            AcceptMultipleSignatures = GetBool(inputs, "webhookAcceptMultipleSignatures"),
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value as string ?? value.ToString();
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return false;
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false,
        };
    }

    private static IReadOnlyList<string>? GetStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            IReadOnlyList<string> list => list,
            IEnumerable<string> seq => seq.ToArray(),
            string single => new[] { single },
            _ => null,
        };
    }

    private static T ParseEnum<T>(string? text, T fallback) where T : struct
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return Enum.TryParse<T>(text, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
