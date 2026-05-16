namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Singleton sidecar registry of custom-scheme names that the webhook
/// pipeline should resolve through DI keyed-services rather than the
/// built-in <see cref="HmacSignatureVerifier"/>.
/// </summary>
/// <remarks>
/// Populated by
/// <c>DashboardServiceCollectionExtensions.AddWebhookSignatureVerifier&lt;TVerifier&gt;</c>
/// at host startup. The pipeline reads the registry on every request to decide
/// whether the manifest's <c>webhookSignatureScheme</c> value should dispatch
/// to a DI-registered verifier.
/// </remarks>
public sealed class WebhookCustomVerifierRegistry
{
    private readonly HashSet<string> _schemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="schemeName"/> matches
    /// a registered custom verifier (case-insensitive).
    /// </summary>
    /// <param name="schemeName">Manifest <c>webhookSignatureScheme</c> value.</param>
    public bool Contains(string? schemeName) =>
        !string.IsNullOrEmpty(schemeName) && _schemes.Contains(schemeName);

    /// <summary>
    /// Registers <paramref name="schemeName"/> as a custom-verifier dispatch
    /// target. Thread-safe enough for one-shot startup wiring.
    /// </summary>
    /// <param name="schemeName">Publisher-specific scheme name (case-insensitive).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the name is null/whitespace, or matches the name of any
    /// built-in <see cref="WebhookSignatureScheme"/> value (case-insensitive),
    /// including the <c>Custom</c> sentinel.
    /// </exception>
    public void Register(string schemeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemeName);
        var collision = Enum.GetNames<WebhookSignatureScheme>()
            .FirstOrDefault(builtIn => string.Equals(schemeName, builtIn, StringComparison.OrdinalIgnoreCase));
        if (collision is not null)
        {
            throw new ArgumentException(
                $"'{schemeName}' collides with the built-in WebhookSignatureScheme.{collision} value; use a publisher-specific name.",
                nameof(schemeName));
        }
        _schemes.Add(schemeName);
    }
}
