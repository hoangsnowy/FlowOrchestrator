namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Verifier abstraction for HMAC-style webhook signatures. Implementations are
/// expected to be pure (no I/O, no allocation of large buffers per call) and
/// thread-safe; one instance is shared across all webhook receives.
/// </summary>
public interface IWebhookSignatureVerifier
{
    /// <summary>
    /// Verifies the signature carried in <paramref name="context"/> against the
    /// spec and key material in the same context.
    /// </summary>
    /// <param name="context">Per-request input including raw body, headers and signing material.</param>
    /// <returns>
    /// <see cref="WebhookSignatureResult.Success"/> when the digest matches with
    /// the current key, <see cref="WebhookSignatureResult.SuccessWithRotation"/>
    /// when it matches the previous key, otherwise a failure with a precise
    /// <see cref="WebhookSignatureFailureReason"/>.
    /// </returns>
    /// <remarks>
    /// Verifier MUST run in constant time across both candidate keys (and across
    /// every signature in a multi-value header) so timing measurements cannot
    /// reveal which key, version, or candidate matched.
    /// </remarks>
    WebhookSignatureResult Verify(WebhookSignatureContext context);
}
