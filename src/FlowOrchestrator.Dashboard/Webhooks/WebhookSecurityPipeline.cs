using System.Diagnostics;
using System.Net;
using FlowOrchestrator.Dashboard.Webhooks.Logging;
using FlowOrchestrator.Dashboard.Webhooks.Network;
using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.Webhooks;

/// <summary>
/// Coordinates the webhook hardening gate chain for a single inbound request.
/// v1.25.0 covers HMAC signature verification + replay protection; later
/// phases plug rate-limit, IP-allowlist, and DLQ persistence into the same
/// pipeline.
/// </summary>
public sealed class WebhookSecurityPipeline
{
    private readonly IWebhookSignatureVerifier _verifier;
    private readonly ReplayProtectionGate? _replayGate;
    private readonly IWebhookRateLimiter? _rateLimiter;
    private readonly WebhookSecurityOptions _options;
    private readonly ILogger<WebhookSecurityPipeline>? _logger;

    /// <summary>Constructs the pipeline with required dependencies.</summary>
    /// <param name="verifier">Signature verifier (typically <see cref="HmacSignatureVerifier"/>).</param>
    /// <param name="options">Operator-supplied webhook security options.</param>
    /// <param name="replayGate">Optional replay-protection gate. <see langword="null"/> when not configured.</param>
    /// <param name="rateLimiter">Optional rate limiter; <see langword="null"/> falls back to "no rate-limit".</param>
    /// <param name="logger">Optional logger for structured event emission.</param>
    public WebhookSecurityPipeline(
        IWebhookSignatureVerifier verifier,
        WebhookSecurityOptions options,
        ReplayProtectionGate? replayGate = null,
        IWebhookRateLimiter? rateLimiter = null,
        ILogger<WebhookSecurityPipeline>? logger = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _replayGate = replayGate;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>Outcome chip emitted by the pipeline.</summary>
    public enum Decision
    {
        /// <summary>Continue with normal trigger dispatch.</summary>
        Accept,

        /// <summary>Reject with the given status code (Enforce mode).</summary>
        Reject,

        /// <summary>Audit mode — log + accept but mark the trace.</summary>
        AuditFail,
    }

    /// <summary>Verdict including the HTTP status code and a precise reason chip.</summary>
    public readonly record struct PipelineResult(Decision Decision, int StatusCode, string Reason, bool UsedRotationKey);

    /// <summary>
    /// Runs the active gate chain (signature → replay). Caller passes the
    /// resolved flow / trigger info plus the buffered body bytes.
    /// </summary>
    /// <param name="http">HTTP context (used for client IP, absolute URL, activity tags).</param>
    /// <param name="flowId">Resolved flow identifier.</param>
    /// <param name="triggerKey">Resolved trigger key (manifest key).</param>
    /// <param name="triggerInputs">Manifest inputs for the matching webhook trigger.</param>
    /// <param name="bodyBytes">Raw body bytes (already capped to MaxBodyBytes).</param>
    /// <param name="headers">Inbound request headers (case-insensitive).</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<PipelineResult> EvaluateAsync(
        HttpContext http,
        Guid flowId,
        string triggerKey,
        IReadOnlyDictionary<string, object?> triggerInputs,
        ReadOnlyMemory<byte> bodyBytes,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default)
    {
        if (_options.EnforcementMode == WebhookEnforcementMode.Off)
            return new PipelineResult(Decision.Accept, StatusCodes.Status200OK, "ok", false);

        var clientIp = ResolveClientIp(http, _options.ForwardedHeaderDepth);
        Activity.Current?.SetTag("flow.webhook.client_ip", clientIp?.ToString() ?? "(unknown)");

        // ── IP allow / deny gate ──
        var ipVerdict = EvaluateIp(triggerInputs, clientIp);
        if (ipVerdict is { } reason)
            return RejectIp(flowId, triggerKey, clientIp, reason);

        // ── Rate-limit gate (per flow [+ IP]) ──
        if (_rateLimiter is not null)
        {
            var rateOpts = ResolveRateLimit(triggerInputs);
            if (rateOpts.IsEnabled)
            {
                var key = rateOpts.PerIp && clientIp is not null
                    ? $"{flowId}|{clientIp}"
                    : flowId.ToString();
                var rl = _rateLimiter.TryAcquire(key, rateOpts);
                if (!rl.Allowed)
                    return RejectRateLimit(flowId, triggerKey, key, rl.RetryAfter);
            }
        }

        var customSchemesRo = _options.CustomSchemes.Count == 0
            ? null
            : (IReadOnlyDictionary<string, WebhookSignatureSpec>)new Dictionary<string, WebhookSignatureSpec>(_options.CustomSchemes, StringComparer.OrdinalIgnoreCase);
        var spec = WebhookSignatureSpecResolver.Resolve(triggerInputs, customSchemesRo);

        // ── Signature gate ──
        var usedRotation = false;
        if (spec is not null)
        {
            var key = TryGetString(triggerInputs, "webhookHmacKey")
                      ?? TryGetString(triggerInputs, "webhookSecret");
            var previous = TryGetString(triggerInputs, "webhookHmacKeyPrevious")
                           ?? TryGetString(triggerInputs, "webhookSecretPrevious");
            if (string.IsNullOrEmpty(key))
            {
                _logger?.LogWarning("Webhook signature scheme set on flow {FlowId} but no key configured.", flowId);
                return RejectSignature(flowId, triggerKey, "key_not_configured");
            }

            var ctx = new WebhookSignatureContext
            {
                Body = bodyBytes,
                Headers = headers,
                AbsoluteUrl = BuildAbsoluteUrl(http),
                FormFields = null,
                Spec = spec,
                HmacKey = key,
                HmacKeyPrevious = previous,
                AllowLegacySha1 = _options.AllowLegacySha1,
            };

            var sigResult = _verifier.Verify(ctx);
            Activity.Current?.SetTag("flow.webhook.scheme", spec.HeaderName);
            if (!sigResult.IsValid)
                return RejectSignature(flowId, triggerKey, sigResult.Reason.ToString());
            if (sigResult.UsedRotationKey && _logger is not null)
                WebhookLog.RotationUsedPreviousKey(_logger, flowId, triggerKey);
            usedRotation = sigResult.UsedRotationKey;
        }

        // ── Replay-protection gate ──
        if (_replayGate is not null)
        {
            var tolerance = TryGetInt(triggerInputs, "webhookReplayToleranceSeconds")
                            ?? _options.ReplayToleranceSeconds;
            var tsHeader = TryGetString(triggerInputs, "webhookTimestampHeader")
                           ?? _options.DefaultTimestampHeader
                           ?? spec?.TimestampHeaderName;
            var nonceHeader = TryGetString(triggerInputs, "webhookNonceHeader")
                              ?? _options.DefaultNonceHeader;

            if (tolerance > 0)
            {
                var verdict = await _replayGate.EvaluateAsync(
                    flowId, triggerKey, headers, bodyBytes, tolerance, tsHeader, nonceHeader, ct).ConfigureAwait(false);

                Activity.Current?.SetTag("flow.webhook.replay_skew_ms", verdict.Skew.TotalMilliseconds);

                switch (verdict.Decision)
                {
                    case ReplayProtectionGate.Decision.SkewRejected:
                    case ReplayProtectionGate.Decision.ReplayRejected:
                    case ReplayProtectionGate.Decision.TimestampMissing:
                        return RejectReplay(flowId, triggerKey, verdict.Reason ?? verdict.Decision.ToString());
                }
            }
        }

        Activity.Current?.SetTag("flow.webhook.result", "accepted");
        return new PipelineResult(Decision.Accept, StatusCodes.Status200OK, "ok", usedRotation);
    }

    private PipelineResult RejectSignature(Guid flowId, string triggerKey, string reason)
    {
        if (_logger is not null) WebhookLog.SignatureRejected(_logger, flowId, triggerKey, reason);
        return Reject(reason, StatusCodes.Status401Unauthorized);
    }

    private PipelineResult RejectReplay(Guid flowId, string triggerKey, string reason)
    {
        if (_logger is not null) WebhookLog.ReplayRejected(_logger, flowId, triggerKey, reason);
        return Reject(reason, StatusCodes.Status409Conflict);
    }

    private PipelineResult Reject(string reason, int statusCode)
    {
        Activity.Current?.SetTag("flow.webhook.result", "rejected");
        Activity.Current?.SetTag("flow.webhook.reject_reason", reason);

        return _options.EnforcementMode switch
        {
            WebhookEnforcementMode.Audit => new PipelineResult(Decision.AuditFail, statusCode, reason, false),
            WebhookEnforcementMode.Enforce => new PipelineResult(Decision.Reject, statusCode, reason, false),
            _ => new PipelineResult(Decision.Accept, StatusCodes.Status200OK, reason, false),
        };
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is not null ? v as string ?? v.ToString() : null;

    private static int? TryGetInt(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    private static string BuildAbsoluteUrl(HttpContext http)
    {
        var req = http.Request;
        return $"{req.Scheme}://{req.Host}{req.PathBase}{req.Path}{req.QueryString}";
    }

    private static IPAddress? ResolveClientIp(HttpContext http, int forwardedDepth)
    {
        if (forwardedDepth > 0
            && http.Request.Headers.TryGetValue("X-Forwarded-For", out var xff)
            && !string.IsNullOrWhiteSpace(xff))
        {
            // X-Forwarded-For: client, proxy1, proxy2 (left-to-right). Trust the
            // last `forwardedDepth` entries as proxies; the entry just before is
            // the client we want.
            var hops = xff.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var index = Math.Max(0, hops.Length - forwardedDepth - 1);
            if (IPAddress.TryParse(hops[index], out var addr)) return addr;
        }
        return http.Connection.RemoteIpAddress;
    }

    private static string? EvaluateIp(IReadOnlyDictionary<string, object?> inputs, IPAddress? clientIp)
    {
        // Allow list takes precedence; if set and the client doesn't match, deny.
        var allowList = ResolveCidrList(inputs, "webhookIpAllowList", "webhookIpAllowListPreset");
        if (allowList is not null)
        {
            var matcher = new CidrMatcher(allowList);
            if (!matcher.IsEmpty && !matcher.Matches(clientIp))
                return "allowlist_miss";
        }

        var denyList = ResolveCidrList(inputs, "webhookIpDenyList", presetKey: null);
        if (denyList is not null)
        {
            var matcher = new CidrMatcher(denyList);
            if (matcher.Matches(clientIp))
                return "denylist_hit";
        }
        return null;
    }

    private static IReadOnlyList<string>? ResolveCidrList(
        IReadOnlyDictionary<string, object?> inputs,
        string listKey,
        string? presetKey)
    {
        if (inputs.TryGetValue(listKey, out var raw) && raw is not null)
        {
            return raw switch
            {
                IReadOnlyList<string> list => list,
                IEnumerable<string> seq => seq.ToArray(),
                string single => new[] { single },
                _ => null,
            };
        }
        if (presetKey is not null && inputs.TryGetValue(presetKey, out var presetRaw) && presetRaw is string preset)
            return KnownPublisherCidrs.TryGet(preset);
        return null;
    }

    private WebhookRateLimitOptions ResolveRateLimit(IReadOnlyDictionary<string, object?> inputs)
    {
        var pps = TryGetDouble(inputs, "webhookRateLimitPermitsPerSecond");
        if (pps is null) return _options.RateLimit;
        return new WebhookRateLimitOptions
        {
            PermitsPerSecond = pps.Value,
            BurstSize = (int?)TryGetDouble(inputs, "webhookRateLimitBurstSize"),
            PerIp = inputs.TryGetValue("webhookRateLimitPerIp", out var perIp) && perIp is bool b && b,
        };
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }

    private PipelineResult RejectIp(Guid flowId, string triggerKey, IPAddress? ip, string reason)
    {
        if (_logger is not null) WebhookLog.IpDenied(_logger, flowId, triggerKey, ip?.ToString() ?? "(unknown)");
        return Reject(reason, StatusCodes.Status403Forbidden);
    }

    private PipelineResult RejectRateLimit(Guid flowId, string triggerKey, string key, TimeSpan retryAfter)
    {
        if (_logger is not null) WebhookLog.RateLimited(_logger, flowId, triggerKey, key);
        Activity.Current?.SetTag("flow.webhook.rate_limit.retry_after_ms", retryAfter.TotalMilliseconds);
        return Reject("rate_limited", StatusCodes.Status429TooManyRequests);
    }
}
