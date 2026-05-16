using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks.Logging;
using FlowOrchestrator.Dashboard.Webhooks.Network;
using FlowOrchestrator.Dashboard.Webhooks.RateLimit;
using FlowOrchestrator.Dashboard.Webhooks.Replay;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IWebhookRejectionStore? _rejectionStore;
    private readonly FlowOrchestratorTelemetry? _telemetry;
    private readonly TimeProvider _clock;
    private readonly WebhookSecurityOptions _options;
    private readonly ILogger<WebhookSecurityPipeline>? _logger;

    /// <summary>
    /// Per-flow latch tracking flows that have already emitted EventId 4011
    /// (both <c>webhookHmacKey</c> and <c>webhookSecret</c> set on the same trigger).
    /// One warning per flow keeps logs quiet under high traffic while still
    /// alerting operators about the misconfiguration.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _warnedConflictFlows = new();

    private readonly Signature.WebhookCustomVerifierRegistry? _customRegistry;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>Constructs the pipeline with required dependencies.</summary>
    /// <param name="verifier">Signature verifier (typically <see cref="HmacSignatureVerifier"/>).</param>
    /// <param name="options">Operator-supplied webhook security options.</param>
    /// <param name="replayGate">Optional replay-protection gate. <see langword="null"/> when not configured.</param>
    /// <param name="rateLimiter">Optional rate limiter; <see langword="null"/> falls back to "no rate-limit".</param>
    /// <param name="rejectionStore">Optional DLQ + recent-deliveries store.</param>
    /// <param name="telemetry">Optional metrics emitter; counters/histograms are no-ops when null.</param>
    /// <param name="clock">Time provider used for receive timestamps.</param>
    /// <param name="logger">Optional logger for structured event emission.</param>
    /// <param name="customRegistry">
    /// Optional sidecar registry of DI-registered custom-verifier scheme names.
    /// When set together with <paramref name="scopeFactory"/>, the pipeline dispatches
    /// matching scheme names to the keyed <see cref="IWebhookSignatureVerifier"/> service.
    /// </param>
    /// <param name="scopeFactory">
    /// Optional <see cref="IServiceScopeFactory"/> used to resolve scoped custom verifiers.
    /// </param>
    public WebhookSecurityPipeline(
        IWebhookSignatureVerifier verifier,
        WebhookSecurityOptions options,
        ReplayProtectionGate? replayGate = null,
        IWebhookRateLimiter? rateLimiter = null,
        IWebhookRejectionStore? rejectionStore = null,
        FlowOrchestratorTelemetry? telemetry = null,
        TimeProvider? clock = null,
        ILogger<WebhookSecurityPipeline>? logger = null,
        Signature.WebhookCustomVerifierRegistry? customRegistry = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _replayGate = replayGate;
        _rateLimiter = rateLimiter;
        _rejectionStore = rejectionStore;
        _telemetry = telemetry;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _customRegistry = customRegistry;
        _scopeFactory = scopeFactory;
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
        var stopwatch = ValueStopwatch.StartNew();
        var clientIp = ResolveClientIp(http, _options.ForwardedHeaderDepth);

        // Always emit body-size histogram + activity tag, even when enforcement off,
        // so dashboards see traffic shape regardless of mode.
        Activity.Current?.SetTag("flow.webhook.bytes", bodyBytes.Length);
        Activity.Current?.SetTag("flow.webhook.client_ip", clientIp?.ToString() ?? "(unknown)");
        _telemetry?.WebhookBodyBytes.Record(bodyBytes.Length, new KeyValuePair<string, object?>("flow", flowId.ToString()));

        if (_options.EnforcementMode == WebhookEnforcementMode.Off)
        {
            EmitAccept(flowId, triggerKey, "off", clientIp, bodyBytes.Length, headers, stopwatch.Elapsed.TotalMilliseconds, scheme: null);
            return new PipelineResult(Decision.Accept, StatusCodes.Status200OK, "ok", false);
        }

        // ── IP allow / deny gate ──
        var ipVerdict = EvaluateIp(triggerInputs, clientIp);
        if (ipVerdict is { } reason)
            return await PersistRejectIpAsync(flowId, triggerKey, clientIp, reason, bodyBytes, headers, stopwatch, ct).ConfigureAwait(false);

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
                    return await PersistRejectRateLimitAsync(flowId, triggerKey, key, rl.RetryAfter, clientIp, bodyBytes, headers, stopwatch, ct).ConfigureAwait(false);
            }
        }

        // Resolve the requested scheme. Built-in enum match → DI-registered custom
        // verifier (T3) → manifest "Custom" shape are checked in that order; the
        // custom-verifier registry pre-validates that user-chosen names do not
        // collide with any built-in WebhookSignatureScheme value.
        var schemeName = TryGetString(triggerInputs, "webhookSignatureScheme");
        var customDispatch = schemeName is not null
            && _customRegistry?.Contains(schemeName) == true
            && _scopeFactory is not null;

        WebhookSignatureSpec? spec = null;
        if (!customDispatch)
        {
            var customSchemesRo = _options.CustomSchemes.Count == 0
                ? null
                : (IReadOnlyDictionary<string, WebhookSignatureSpec>)new Dictionary<string, WebhookSignatureSpec>(_options.CustomSchemes, StringComparer.OrdinalIgnoreCase);
            spec = WebhookSignatureSpecResolver.Resolve(triggerInputs, customSchemesRo);
        }

        // ── Signature gate ──
        var usedRotation = false;
        if (spec is not null || customDispatch)
        {
            var (key, previous, conflicted) = ResolveHmacKeys(triggerInputs);
            if (conflicted && _logger is not null && _warnedConflictFlows.TryAdd(flowId, 0))
                WebhookLog.ConflictingKeyFields(_logger, flowId, triggerKey);

            // Built-in path requires a key; custom verifiers may be keyless
            // (KMS, asymmetric, etc.) so they handle credential checks themselves.
            if (!customDispatch && string.IsNullOrEmpty(key))
            {
                _logger?.LogWarning("Webhook signature scheme set on flow {FlowId} but no key configured.", flowId);
                return await PersistRejectSignatureAsync(flowId, triggerKey, "key_not_configured", clientIp, bodyBytes, headers, spec, stopwatch, ct).ConfigureAwait(false);
            }

            var sigCtx = new WebhookSignatureContext
            {
                Body = bodyBytes,
                Headers = headers,
                AbsoluteUrl = BuildAbsoluteUrl(http),
                FormFields = null,
                Spec = spec,
                HmacKey = key ?? string.Empty,
                HmacKeyPrevious = previous,
                AllowLegacySha1 = _options.AllowLegacySha1,
            };

            WebhookSignatureResult sigResult;
            if (customDispatch)
            {
                using var scope = _scopeFactory!.CreateScope();
                var customVerifier = scope.ServiceProvider.GetRequiredKeyedService<IWebhookSignatureVerifier>(schemeName!);
                sigResult = customVerifier.Verify(sigCtx);
            }
            else
            {
                sigResult = _verifier.Verify(sigCtx);
            }

            Activity.Current?.SetTag("flow.webhook.scheme", spec?.HeaderName ?? schemeName);
            if (!sigResult.IsValid)
                return await PersistRejectSignatureAsync(flowId, triggerKey, sigResult.Reason.ToString(), clientIp, bodyBytes, headers, spec, stopwatch, ct).ConfigureAwait(false);
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
                        return await PersistRejectReplayAsync(flowId, triggerKey, verdict.Reason ?? verdict.Decision.ToString(), clientIp, bodyBytes, headers, spec, stopwatch, ct).ConfigureAwait(false);
                }
            }
        }

        Activity.Current?.SetTag("flow.webhook.result", "accepted");
        EmitAccept(flowId, triggerKey, "accepted", clientIp, bodyBytes.Length, headers, stopwatch.Elapsed.TotalMilliseconds, spec?.HeaderName);
        return new PipelineResult(Decision.Accept, StatusCodes.Status200OK, "ok", usedRotation);
    }

    private async ValueTask<PipelineResult> PersistRejectSignatureAsync(
        Guid flowId, string triggerKey, string reason, IPAddress? ip,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        WebhookSignatureSpec? spec, ValueStopwatch sw, CancellationToken ct)
    {
        if (_logger is not null) WebhookLog.SignatureRejected(_logger, flowId, triggerKey, reason);
        return await PersistRejectAsync(flowId, triggerKey, reason, StatusCodes.Status401Unauthorized, ip, body, headers, spec, sw, ct).ConfigureAwait(false);
    }

    private async ValueTask<PipelineResult> PersistRejectReplayAsync(
        Guid flowId, string triggerKey, string reason, IPAddress? ip,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        WebhookSignatureSpec? spec, ValueStopwatch sw, CancellationToken ct)
    {
        if (_logger is not null) WebhookLog.ReplayRejected(_logger, flowId, triggerKey, reason);
        return await PersistRejectAsync(flowId, triggerKey, reason, StatusCodes.Status409Conflict, ip, body, headers, spec, sw, ct).ConfigureAwait(false);
    }

    private async ValueTask<PipelineResult> PersistRejectIpAsync(
        Guid flowId, string triggerKey, IPAddress? ip, string reason,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        ValueStopwatch sw, CancellationToken ct)
    {
        if (_logger is not null) WebhookLog.IpDenied(_logger, flowId, triggerKey, ip?.ToString() ?? "(unknown)");
        return await PersistRejectAsync(flowId, triggerKey, reason, StatusCodes.Status403Forbidden, ip, body, headers, spec: null, sw, ct).ConfigureAwait(false);
    }

    private async ValueTask<PipelineResult> PersistRejectRateLimitAsync(
        Guid flowId, string triggerKey, string clientKey, TimeSpan retryAfter, IPAddress? ip,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        ValueStopwatch sw, CancellationToken ct)
    {
        if (_logger is not null) WebhookLog.RateLimited(_logger, flowId, triggerKey, clientKey);
        Activity.Current?.SetTag("flow.webhook.rate_limit.retry_after_ms", retryAfter.TotalMilliseconds);
        return await PersistRejectAsync(flowId, triggerKey, "rate_limited", StatusCodes.Status429TooManyRequests, ip, body, headers, spec: null, sw, ct).ConfigureAwait(false);
    }

    private async ValueTask<PipelineResult> PersistRejectAsync(
        Guid flowId, string triggerKey, string reason, int statusCode, IPAddress? ip,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        WebhookSignatureSpec? spec, ValueStopwatch sw, CancellationToken ct)
    {
        Activity.Current?.SetTag("flow.webhook.result", "rejected");
        Activity.Current?.SetTag("flow.webhook.reject_reason", reason);

        var processingMs = sw.Elapsed.TotalMilliseconds;
        var flowTag = new KeyValuePair<string, object?>("flow", flowId.ToString());
        var resultTag = new KeyValuePair<string, object?>("result", "rejected");
        var reasonTag = new KeyValuePair<string, object?>("reason", reason);
        var schemeTag = new KeyValuePair<string, object?>("scheme", spec?.HeaderName ?? "(none)");
        _telemetry?.WebhookReceivedCounter.Add(1, flowTag, resultTag, schemeTag);
        _telemetry?.WebhookRejectedCounter.Add(1, flowTag, reasonTag);
        _telemetry?.WebhookProcessingMs.Record(processingMs, flowTag, resultTag);

        if (_rejectionStore is not null)
        {
            try
            {
                await _rejectionStore.WriteAsync(BuildRecord(flowId, triggerKey, reason, statusCode, ip, body, headers, spec, processingMs, isAccepted: false), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (_logger is not null)
            {
                WebhookLog.RejectionStoreFailed(_logger, ex, flowId, triggerKey);
            }
        }

        return _options.EnforcementMode switch
        {
            WebhookEnforcementMode.Audit => new PipelineResult(Decision.AuditFail, statusCode, reason, false),
            WebhookEnforcementMode.Enforce => new PipelineResult(Decision.Reject, statusCode, reason, false),
            _ => new PipelineResult(Decision.Accept, StatusCodes.Status200OK, reason, false),
        };
    }

    private void EmitAccept(
        Guid flowId, string triggerKey, string result, IPAddress? ip, long bytes,
        IReadOnlyDictionary<string, string> headers, double processingMs, string? scheme)
    {
        var flowTag = new KeyValuePair<string, object?>("flow", flowId.ToString());
        var resultTag = new KeyValuePair<string, object?>("result", result);
        var schemeTag = new KeyValuePair<string, object?>("scheme", scheme ?? "(none)");
        _telemetry?.WebhookReceivedCounter.Add(1, flowTag, resultTag, schemeTag);
        _telemetry?.WebhookProcessingMs.Record(processingMs, flowTag, resultTag);

        if (_rejectionStore is not null)
        {
            try
            {
                var rec = BuildRecord(flowId, triggerKey, reason: string.Empty, StatusCodes.Status200OK, ip, body: ReadOnlyMemory<byte>.Empty, headers, spec: null, processingMs, isAccepted: true);
                _ = _rejectionStore.WriteAsync(rec with { BodyBytes = (int)bytes, BodyTruncated = null, Scheme = scheme }, default);
            }
            catch (Exception ex) when (_logger is not null)
            {
                WebhookLog.RejectionStoreFailed(_logger, ex, flowId, triggerKey);
            }
        }
    }

    private WebhookRejectionRecord BuildRecord(
        Guid flowId, string triggerKey, string reason, int statusCode, IPAddress? ip,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        WebhookSignatureSpec? spec, double processingMs, bool isAccepted)
    {
        var truncated = body.IsEmpty
            ? null
            : System.Text.Encoding.UTF8.GetString(body.Span.Slice(0, Math.Min(body.Length, 4096)));
        string? headersJson = null;
        try
        {
            headersJson = JsonSerializer.Serialize(headers);
        }
        catch (JsonException) { /* headers shouldn't fail to serialize but stay defensive */ }
        return new WebhookRejectionRecord
        {
            FlowId = flowId,
            TriggerKey = triggerKey,
            ReceivedAt = _clock.GetUtcNow(),
            RemoteIp = ip?.ToString(),
            Reason = reason,
            StatusCode = statusCode,
            BodyBytes = body.Length,
            BodyTruncated = truncated,
            HeadersJson = headersJson,
            Scheme = spec?.HeaderName,
            ProcessingMs = processingMs,
            IsAccepted = isAccepted,
        };
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is not null ? v as string ?? v.ToString() : null;

    /// <summary>
    /// Resolves the HMAC key pair from manifest inputs. Prefers the v1.25 names
    /// (<c>webhookHmacKey</c> / <c>webhookHmacKeyPrevious</c>) and falls back to
    /// the legacy v1.24 names (<c>webhookSecret</c> / <c>webhookSecretPrevious</c>).
    /// The returned <c>Conflicted</c> flag is <see langword="true"/> when both
    /// the modern and legacy current-key fields are populated, signalling that
    /// the operator should drop the legacy field.
    /// </summary>
    internal static (string? Key, string? Previous, bool Conflicted) ResolveHmacKeys(
        IReadOnlyDictionary<string, object?> inputs)
    {
        var modern = TryGetString(inputs, "webhookHmacKey");
        var legacy = TryGetString(inputs, "webhookSecret");
        var modernPrev = TryGetString(inputs, "webhookHmacKeyPrevious");
        var legacyPrev = TryGetString(inputs, "webhookSecretPrevious");
        var conflicted = !string.IsNullOrEmpty(modern) && !string.IsNullOrEmpty(legacy);
        return (modern ?? legacy, modernPrev ?? legacyPrev, conflicted);
    }

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
        var entries = new List<string>();

        // Explicit list — array, IEnumerable, or comma-delimited string.
        if (inputs.TryGetValue(listKey, out var raw) && raw is not null)
        {
            switch (raw)
            {
                case string single:
                    foreach (var part in single.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        entries.Add(part);
                    break;
                case IEnumerable<string> seq:
                    entries.AddRange(seq);
                    break;
                case System.Collections.IEnumerable enumerable:
                    foreach (var o in enumerable)
                    {
                        if (o is string s) entries.Add(s);
                    }
                    break;
            }
        }

        // Preset(s) — single preset name (`webhookIpAllowListPreset`) OR multiple
        // (`webhookIpAllowListPresets` plural; comma-delimited or array). Merge with explicit list.
        if (presetKey is not null)
        {
            // Singular form (back-compat).
            if (inputs.TryGetValue(presetKey, out var presetRaw) && presetRaw is string preset)
            {
                var resolved = KnownPublisherCidrs.TryGet(preset);
                if (resolved is not null) entries.AddRange(resolved);
            }
            // Plural form: same key + 's', supports list or CSV.
            var pluralKey = presetKey + "s";
            if (inputs.TryGetValue(pluralKey, out var pluralRaw) && pluralRaw is not null)
            {
                IEnumerable<string>? names = pluralRaw switch
                {
                    string s => s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    IEnumerable<string> seq => seq,
                    System.Collections.IEnumerable enumerable => enumerable.OfType<string>(),
                    _ => null,
                };
                if (names is not null)
                {
                    // codeql[cs/linq/missed-select] loop has side effects beyond projection: AddRange merges each resolved preset's entries into the outer `entries` accumulator.
                    foreach (var n in names)
                    {
                        var resolved = KnownPublisherCidrs.TryGet(n);
                        if (resolved is not null) entries.AddRange(resolved);
                    }
                }
            }
        }

        return entries.Count == 0 ? null : entries;
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

    /// <summary>Compact stopwatch-equivalent without allocation.</summary>
    private readonly struct ValueStopwatch
    {
        private readonly long _start;
        private ValueStopwatch(long start) { _start = start; }
        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
    }
}
