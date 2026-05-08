namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Outcome of a single signature verification call. Distinct
/// <see cref="WebhookSignatureFailureReason"/> values let the dashboard / DLQ
/// surface a precise reason chip without exposing key material or the digest.
/// </summary>
public readonly record struct WebhookSignatureResult(
    bool IsValid,
    WebhookSignatureFailureReason Reason,
    bool UsedRotationKey)
{
    /// <summary>Successful verification with the current key.</summary>
    public static WebhookSignatureResult Success { get; } =
        new(true, WebhookSignatureFailureReason.None, UsedRotationKey: false);

    /// <summary>Successful verification using the previous (rotated-out) key.</summary>
    public static WebhookSignatureResult SuccessWithRotation { get; } =
        new(true, WebhookSignatureFailureReason.None, UsedRotationKey: true);

    /// <summary>Builds a failure result with the given reason.</summary>
    /// <param name="reason">Specific failure category for telemetry / DLQ.</param>
    public static WebhookSignatureResult Failure(WebhookSignatureFailureReason reason) =>
        new(false, reason, UsedRotationKey: false);
}

/// <summary>
/// Sub-reason taxonomy for a rejected signature. Mapped to log scope tags and
/// DLQ rows; never returned in the HTTP response body.
/// </summary>
public enum WebhookSignatureFailureReason
{
    /// <summary>Verification succeeded.</summary>
    None = 0,

    /// <summary>Required header is missing from the request.</summary>
    MissingHeader,

    /// <summary>Header value is malformed (multi-value parse failure, prefix mismatch, etc.).</summary>
    MalformedHeader,

    /// <summary>Spec asks for a key/version not present in the multi-value header.</summary>
    UnsupportedVersion,

    /// <summary>Encoding (hex/base64) parse failed.</summary>
    InvalidEncoding,

    /// <summary>Algorithm rejected because legacy SHA-1 is disabled.</summary>
    AlgorithmNotAllowed,

    /// <summary>Required timestamp header is missing or empty.</summary>
    MissingTimestamp,

    /// <summary>Provided timestamp is not parseable as a Unix-seconds integer.</summary>
    InvalidTimestamp,

    /// <summary>HMAC key is empty / null on the spec — endpoint mis-configured.</summary>
    KeyNotConfigured,

    /// <summary>Request URL is required by the strategy but not supplied.</summary>
    AbsoluteUrlRequired,

    /// <summary>HMAC digest did not match any presented signature with either current or previous key.</summary>
    DigestMismatch,
}
