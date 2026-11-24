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
        var spec = context.Spec
            ?? throw new ArgumentException(
                "HmacSignatureVerifier requires WebhookSignatureContext.Spec; null spec is reserved for DI-registered custom verifiers.",
                nameof(context));
        if (spec.Algorithm == HmacAlgorithm.Sha1 && !context.AllowLegacySha1)
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.AlgorithmNotAllowed);

        if (string.IsNullOrEmpty(context.HmacKey))
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.KeyNotConfigured);

        // Special case: Mailgun-style schemes carry the signature in the JSON body, not headers.
        if (spec.SignedPayloadStrategy == SignedPayloadStrategy.TimestampPlusToken)
            return VerifyBodyResident(context, spec);

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

        // Standard Webhooks: the message id is part of the signed content (and becomes the replay key).
        string? messageId = null;
        if (spec.SignedPayloadStrategy == SignedPayloadStrategy.MessageIdTimestampBody)
        {
            if (string.IsNullOrEmpty(spec.MessageIdHeaderName)
                || !context.Headers.TryGetValue(spec.MessageIdHeaderName, out var idHeader)
                || string.IsNullOrWhiteSpace(idHeader))
            {
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.MissingHeader);
            }
            messageId = idHeader.Trim();
        }

        // Build the byte sequence the digest covers.
        if (!TryBuildSignedPayload(context, spec, timestamp, messageId, out var signedPayload, out var buildFailure))
            return WebhookSignatureResult.Failure(buildFailure);

        // Compute expected digests for current key and (optionally) previous key.
        if (!TryGetKeyBytes(context.HmacKey!, spec.KeyEncoding, out var currentKey))
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.KeyNotConfigured);
        var currentDigest = ComputeHmac(spec.Algorithm, currentKey, signedPayload);
        // An undecodable previous key is treated as absent: the rotation window simply does not apply.
        var previousDigest = !string.IsNullOrEmpty(context.HmacKeyPrevious)
            && TryGetKeyBytes(context.HmacKeyPrevious!, spec.KeyEncoding, out var previousKey)
            ? ComputeHmac(spec.Algorithm, previousKey, signedPayload)
            : null;

        // Constant-time compare across every candidate × every key.
        // The match flags accumulate via | so we don't short-circuit and we
        // run every comparison regardless of an early hit — security requirement,
        // a Where().Any() would short-circuit and leak timing information.
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

        if (!matchedCurrent && !matchedPrevious)
            return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.DigestMismatch);

        // Only strategies whose signed payload includes the timestamp authenticate it. A timestamp that was
        // merely required (RequireTimestamp) but is not part of the digest proves nothing about freshness.
        var timestampIsSigned = spec.SignedPayloadStrategy is SignedPayloadStrategy.TimestampDotBody
            or SignedPayloadStrategy.ColonDelimited
            or SignedPayloadStrategy.MessageIdTimestampBody;
        var result = matchedCurrent ? WebhookSignatureResult.Success : WebhookSignatureResult.SuccessWithRotation;
        if (!timestampIsSigned)
            return result;
        // messageId is non-null only for MessageIdTimestampBody, where the digest covers it: it is the replay key.
        return result with { AuthenticatedTimestamp = timestamp, AuthenticatedNonce = messageId };
    }

    private WebhookSignatureResult VerifyBodyResident(WebhookSignatureContext context, WebhookSignatureSpec spec)
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
            if (!TryGetKeyBytes(context.HmacKey!, spec.KeyEncoding, out var currentKey))
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.KeyNotConfigured);
            var currentDigest = ComputeHmac(spec.Algorithm, currentKey, signedPayload);
            var previousDigest = !string.IsNullOrEmpty(context.HmacKeyPrevious)
                && TryGetKeyBytes(context.HmacKeyPrevious!, spec.KeyEncoding, out var previousKey)
                ? ComputeHmac(spec.Algorithm, previousKey, signedPayload)
                : null;

            if (!TryDecode(candidate, spec.Encoding, out var candidateBytes))
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.InvalidEncoding);

            var matchedCurrent = CryptographicOperations.FixedTimeEquals(candidateBytes, currentDigest);
            var matchedPrevious = previousDigest is not null
                && CryptographicOperations.FixedTimeEquals(candidateBytes, previousDigest);

            if (!matchedCurrent && !matchedPrevious)
                return WebhookSignatureResult.Failure(WebhookSignatureFailureReason.DigestMismatch);

            // Mailgun signs timestamp + token (not the body): both are authenticated.
            var verified = matchedCurrent ? WebhookSignatureResult.Success : WebhookSignatureResult.SuccessWithRotation;
            return verified with { AuthenticatedTimestamp = timestamp, AuthenticatedNonce = token };
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
            // Stripe / Calendly: "t=<ts>,v1=<sig>,v0=<sig>"; Standard Webhooks: "v1,<sig> v1,<sig> v2a,<sig>"
            var segments = headerValue.Split(spec.MultiValueDelimiter, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var sep = spec.KeyValueSeparator ?? "=";
            foreach (var segment in segments)
            {
                var sepIndex = segment.IndexOf(sep, StringComparison.Ordinal);
                if (sepIndex <= 0)
                {
                    // Standard Webhooks: a malformed entry is just a candidate that can never match.
                    if (spec.IgnoreMalformedEntries)
                        continue;
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
        string? messageId,
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

            case SignedPayloadStrategy.MessageIdTimestampBody:
                if (string.IsNullOrEmpty(timestamp))
                {
                    failure = WebhookSignatureFailureReason.MissingTimestamp;
                    return false;
                }
                if (string.IsNullOrEmpty(messageId))
                {
                    failure = WebhookSignatureFailureReason.MissingHeader;
                    return false;
                }
                payload = SignedPayloadBuilder.MessageIdTimestampBody(
                    messageId, timestamp, context.Body, spec.SignedPayloadDelimiter ?? ".");
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

    /// <summary>
    /// Turns the configured key string into HMAC key bytes per <paramref name="encoding"/>.
    /// </summary>
    /// <returns><see langword="false"/> when a <see cref="HmacKeyEncoding.Base64"/> key is not valid base64.</returns>
    private static bool TryGetKeyBytes(string key, HmacKeyEncoding encoding, out byte[] keyBytes)
    {
        if (encoding != HmacKeyEncoding.Base64)
        {
            keyBytes = Encoding.UTF8.GetBytes(key);
            return true;
        }

        // Microsoft Teams: the HMAC is keyed with the base64-DECODED security token; Standard Webhooks: with
        // "the base64 decoded part after whsec_" (see both entries in PartnerSchemeRegistry for the references).
        // The prefix is optional — the reference libraries accept the secret with or without it.
        var trimmed = key.AsSpan().Trim();
        if (trimmed.StartsWith(StandardWebhooksSecretPrefix, StringComparison.Ordinal))
            trimmed = trimmed[StandardWebhooksSecretPrefix.Length..];

        // Decode straight from the span: no substring, no FormatException on a mis-pasted key.
        var buffer = new byte[(trimmed.Length * 3 / 4) + 3];
        var decoded = Convert.TryFromBase64Chars(trimmed, buffer, out var written) && written > 0;
        keyBytes = decoded ? buffer.AsSpan(0, written).ToArray() : Array.Empty<byte>();
        CryptographicOperations.ZeroMemory(buffer);
        return decoded;
    }

    /// <summary>Prefix Standard Webhooks puts in front of the base64 secret "for easy identification".</summary>
    private const string StandardWebhooksSecretPrefix = "whsec_";

    private static byte[] ComputeHmac(HmacAlgorithm algorithm, byte[] keyBytes, byte[] payload)
    {
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
