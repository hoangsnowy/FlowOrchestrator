using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlowOrchestrator.Dashboard.Webhooks.Signature;

/// <summary>
/// Generic HMAC signature verifier driven by a <see cref="WebhookSignatureSpec"/>.
/// One instance handles every built-in partner scheme plus user-defined custom
/// specs by switching on the spec fields rather than per-publisher subclasses.
/// </summary>
/// <remarks>
/// Verification is constant-time: every candidate signature in a multi-value
/// header is HMACed against both current and previous keys before a final
/// boolean is returned. Short-circuiting would expose which candidate matched
/// (or that the previous key was used) through wall-clock timing.
/// </remarks>
public sealed class HmacSignatureVerifier : IWebhookSignatureVerifier
{
    private readonly IReadOnlyDictionary<string, Func<WebhookSignatureContext, byte[]>>? _customStrategies;

    /// <summary>Creates a verifier with no user-registered custom strategies.</summary>
    public HmacSignatureVerifier()
        : this(customStrategies: null)
    {
    }

    /// <summary>Creates a verifier with optional user-registered custom strategies.</summary>
    /// <param name="customStrategies">
    /// Map of strategy-name → byte-builder used when
    /// <see cref="WebhookSignatureSpec.SignedPayloadStrategy"/> equals
    /// <see cref="SignedPayloadStrategy.Custom"/>.
    /// </param>
    public HmacSignatureVerifier(
        IReadOnlyDictionary<string, Func<WebhookSignatureContext, byte[]>>? customStrategies)
    {
        _customStrategies = customStrategies;
    }

    /// <inheritdoc/>
    public WebhookSignatureResult Verify(WebhookSignatureContext context)
    {
        var spec = context.Spec;
        if (spec.Algorithm == HmacAlgorithm.Sha1 && !context.AllowLegacySha1)
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.AlgorithmNotAllowed);

        if (string.IsNullOrEmpty(context.HmacKey))
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.KeyNotConfigured);

        // Special case: Mailgun-style schemes carry the signature in the JSON body, not headers.
        if (spec.SignedPayloadStrategy == SignedPayloadStrategy.TimestampPlusToken)
            return VerifyBodyResident(context);

        // Pull header value (or built-in body-resident exception above).
        if (!TryReadHeader(context, spec, out var headerValue, out var headerFailure))
            return WebhookSignatureResult.Failure(headerFailure);

        // Strip any HeaderValuePrefix (e.g. "HMAC " on Microsoft Teams).
        if (!string.IsNullOrEmpty(spec.HeaderValuePrefix))
        {
            if (!headerValue.StartsWith(spec.HeaderValuePrefix, StringComparison.OrdinalIgnoreCase))
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MalformedHeader);
            headerValue = headerValue[spec.HeaderValuePrefix.Length..].Trim();
        }

        // Parse out signature candidates and the optional timestamp.
        if (!TryParseCandidates(headerValue, spec, out var candidates, out var timestamp, out var parseFailure))
            return WebhookSignatureResult.Failure(parseFailure);

        if (spec.RequireTimestamp && string.IsNullOrEmpty(timestamp))
        {
            // Try the spec's TimestampHeaderName fallback.
            if (!string.IsNullOrEmpty(spec.TimestampHeaderName)
                && context.Headers.TryGetValue(spec.TimestampHeaderName, out var tsHeader)
                && !string.IsNullOrWhiteSpace(tsHeader))
            {
                timestamp = tsHeader.Trim();
            }
            else
            {
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MissingTimestamp);
            }
        }

        // Build the byte sequence the digest covers.
        if (!TryBuildSignedPayload(context, spec, timestamp, out var signedPayload, out var buildFailure))
            return WebhookSignatureResult.Failure(buildFailure);

        // Compute expected digests for current key and (optionally) previous key.
        var currentDigest = ComputeHmac(spec.Algorithm, context.HmacKey!, signedPayload);
        var previousDigest = !string.IsNullOrEmpty(context.HmacKeyPrevious)
            ? ComputeHmac(spec.Algorithm, context.HmacKeyPrevious!, signedPayload)
            : null;

        // Constant-time compare across every candidate × every key.
        // The match flags accumulate via | so we don't short-circuit and we
        // run every comparison regardless of an early hit.
        var matchedCurrent = false;
        var matchedPrevious = false;
        foreach (var candidate in candidates)
        {
            if (!TryDecode(candidate, spec.Encoding, out var candidateBytes))
                continue;

            matchedCurrent |= CryptographicOperations.FixedTimeEquals(candidateBytes, currentDigest);
            if (previousDigest is not null)
                matchedPrevious |= CryptographicOperations.FixedTimeEquals(candidateBytes, previousDigest);
        }

        if (matchedCurrent)
            return WebhookSignatureResult.Success;
        if (matchedPrevious)
            return WebhookSignatureResult.SuccessWithRotation;

        return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.DigestMismatch);
    }

    private WebhookSignatureResult VerifyBodyResident(WebhookSignatureContext context)
    {
        // Mailgun shape: { "signature": { "timestamp": "...", "token": "...", "signature": "<hex>" } }
        // Body is JSON; we read these three values without re-parsing the whole tree.
        try
        {
            using var doc = JsonDocument.Parse(context.Body);
            if (!doc.RootElement.TryGetProperty("signature", out var sigBlock) || sigBlock.ValueKind != JsonValueKind.Object)
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MalformedHeader);
            if (!sigBlock.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MissingTimestamp);
            if (!sigBlock.TryGetProperty("token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MalformedHeader);
            if (!sigBlock.TryGetProperty("signature", out var sigEl) || sigEl.ValueKind != JsonValueKind.String)
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MissingHeader);

            var timestamp = tsEl.GetString()!;
            var token = tokenEl.GetString()!;
            var candidate = sigEl.GetString()!;

            var signedPayload = SignedPayloadBuilder.TimestampPlusToken(timestamp, token);
            var currentDigest = ComputeHmac(context.Spec.Algorithm, context.HmacKey!, signedPayload);
            var previousDigest = !string.IsNullOrEmpty(context.HmacKeyPrevious)
                ? ComputeHmac(context.Spec.Algorithm, context.HmacKeyPrevious!, signedPayload)
                : null;

            if (!TryDecode(candidate, context.Spec.Encoding, out var candidateBytes))
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.InvalidEncoding);

            var matchedCurrent = CryptographicOperations.FixedTimeEquals(candidateBytes, currentDigest);
            var matchedPrevious = previousDigest is not null
                && CryptographicOperations.FixedTimeEquals(candidateBytes, previousDigest);

            if (matchedCurrent) return WebhookSignatureResult.Success;
            if (matchedPrevious) return WebhookSignatureResult.SuccessWithRotation;
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.DigestMismatch);
        }
        catch (JsonException)
        {
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MalformedHeader);
        }
    }

    private static bool TryReadHeader(
        WebhookSignatureContext context,
        WebhookSignatureSpec spec,
        out string headerValue,
        out WebhookSignatureFailureReason failure)
    {
        headerValue = string.Empty;
        failure = WebhookSignatureFailureReason.None;
        if (string.IsNullOrEmpty(spec.HeaderName))
        {
            failure = WebhookSignatureFailureReason.MissingHeader;
            return false;
        }
        if (!context.Headers.TryGetValue(spec.HeaderName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failure = WebhookSignatureFailureReason.MissingHeader;
            return false;
        }
        headerValue = raw.Trim();
        return true;
    }

    private static bool TryParseCandidates(
        string headerValue,
        WebhookSignatureSpec spec,
        out List<string> candidates,
        out string? timestamp,
        out WebhookSignatureFailureReason failure)
    {
        candidates = new List<string>();
        timestamp = null;
        failure = WebhookSignatureFailureReason.None;

        if (!string.IsNullOrEmpty(spec.MultiValueDelimiter))
        {
            // Stripe / Calendly: "t=<ts>,v1=<sig>,v0=<sig>"
            var segments = headerValue.Split(spec.MultiValueDelimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var sep = spec.KeyValueSeparator ?? "=";
            foreach (var segment in segments)
            {
                var sepIndex = segment.IndexOf(sep, StringComparison.Ordinal);
                if (sepIndex <= 0)
                {
                    failure = WebhookSignatureFailureReason.MalformedHeader;
                    return false;
                }
                var key = segment[..sepIndex];
                var value = segment[(sepIndex + sep.Length)..];
                if (!string.IsNullOrEmpty(spec.TimestampValueKey)
                    && string.Equals(key, spec.TimestampValueKey, StringComparison.Ordinal))
                {
                    timestamp = value;
                    continue;
                }
                if (spec.AcceptedVersions is { Count: > 0 } versions
                    && !versions.Contains(key, StringComparer.Ordinal))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(spec.SignatureValueKey)
                    && !string.Equals(key, spec.SignatureValueKey, StringComparison.Ordinal))
                {
                    continue;
                }
                candidates.Add(value);
            }
            if (candidates.Count == 0)
            {
                failure = WebhookSignatureFailureReason.UnsupportedVersion;
                return false;
            }
            return true;
        }

        // Single-value header. Strip Prefix (e.g. "sha256=") if configured.
        var single = headerValue;
        if (!string.IsNullOrEmpty(spec.Prefix))
        {
            if (!single.StartsWith(spec.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                failure = WebhookSignatureFailureReason.MalformedHeader;
                return false;
            }
            single = single[spec.Prefix.Length..];
        }
        candidates.Add(single);
        return true;
    }

    private bool TryBuildSignedPayload(
        WebhookSignatureContext context,
        WebhookSignatureSpec spec,
        string? timestamp,
        out byte[] payload,
        out WebhookSignatureFailureReason failure)
    {
        payload = Array.Empty<byte>();
        failure = WebhookSignatureFailureReason.None;
        switch (spec.SignedPayloadStrategy)
        {
            case SignedPayloadStrategy.RawBody:
                payload = SignedPayloadBuilder.RawBody(context.Body);
                return true;

            case SignedPayloadStrategy.TimestampDotBody:
                if (string.IsNullOrEmpty(timestamp))
                {
                    failure = WebhookSignatureFailureReason.MissingTimestamp;
                    return false;
                }
                payload = SignedPayloadBuilder.TimestampDotBody(
                    timestamp, context.Body, spec.SignedPayloadDelimiter ?? ".");
                return true;

            case SignedPayloadStrategy.ColonDelimited:
                if (string.IsNullOrEmpty(timestamp))
                {
                    failure = WebhookSignatureFailureReason.MissingTimestamp;
                    return false;
                }
                if (string.IsNullOrEmpty(spec.SignedPayloadVersion))
                {
                    failure = WebhookSignatureFailureReason.MalformedHeader;
                    return false;
                }
                payload = SignedPayloadBuilder.ColonDelimited(
                    spec.SignedPayloadVersion, timestamp, context.Body, spec.SignedPayloadDelimiter ?? ":");
                return true;

            case SignedPayloadStrategy.UrlPlusSortedForm:
                if (string.IsNullOrEmpty(context.AbsoluteUrl))
                {
                    failure = WebhookSignatureFailureReason.AbsoluteUrlRequired;
                    return false;
                }
                payload = SignedPayloadBuilder.UrlPlusSortedForm(
                    context.AbsoluteUrl, context.FormFields ?? new Dictionary<string, string>());
                return true;

            case SignedPayloadStrategy.UrlPlusBody:
                if (string.IsNullOrEmpty(context.AbsoluteUrl))
                {
                    failure = WebhookSignatureFailureReason.AbsoluteUrlRequired;
                    return false;
                }
                payload = SignedPayloadBuilder.UrlPlusBody(context.AbsoluteUrl, context.Body);
                return true;

            case SignedPayloadStrategy.TimestampPlusToken:
                // Handled by VerifyBodyResident; this branch only reached for misconfigured custom specs.
                failure = WebhookSignatureFailureReason.MalformedHeader;
                return false;

            case SignedPayloadStrategy.Custom:
                if (string.IsNullOrEmpty(spec.CustomStrategyName)
                    || _customStrategies is null
                    || !_customStrategies.TryGetValue(spec.CustomStrategyName, out var fn))
                {
                    failure = WebhookSignatureFailureReason.MalformedHeader;
                    return false;
                }
                payload = fn(context);
                return true;

            default:
                failure = WebhookSignatureFailureReason.MalformedHeader;
                return false;
        }
    }

    private static byte[] ComputeHmac(HmacAlgorithm algorithm, string key, byte[] payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return algorithm switch
        {
            HmacAlgorithm.Sha1 => HMACSHA1.HashData(keyBytes, payload),
            HmacAlgorithm.Sha256 => HMACSHA256.HashData(keyBytes, payload),
            HmacAlgorithm.Sha384 => HMACSHA384.HashData(keyBytes, payload),
            HmacAlgorithm.Sha512 => HMACSHA512.HashData(keyBytes, payload),
            _ => throw new InvalidOperationException($"Unsupported HMAC algorithm: {algorithm}"),
        };
    }

    private static bool TryDecode(string candidate, SignatureEncoding encoding, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var trimmed = candidate.Trim();
        try
        {
            switch (encoding)
            {
                case SignatureEncoding.HexLower:
                case SignatureEncoding.HexUpper:
                    if (trimmed.Length % 2 != 0) return false;
                    bytes = Convert.FromHexString(trimmed);
                    return true;
                case SignatureEncoding.Base64:
                    bytes = Convert.FromBase64String(trimmed);
                    return true;
                case SignatureEncoding.Base64Url:
                    // Restore padding and translate URL-safe alphabet.
                    var padded = trimmed.Replace('-', '+').Replace('_', '/');
                    var pad = padded.Length % 4;
                    if (pad == 2) padded += "==";
                    else if (pad == 3) padded += "=";
                    else if (pad == 1) return false;
                    bytes = Convert.FromBase64String(padded);
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
