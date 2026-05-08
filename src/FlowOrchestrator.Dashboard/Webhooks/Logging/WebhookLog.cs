using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.Webhooks.Logging;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> methods for the webhook hardening
/// pipeline. EventIds 4000–4099 are reserved for webhook events; subscribers can
/// filter on <c>EventId.Id</c> to capture every signal from the gate chain.
/// </summary>
internal static partial class WebhookLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Webhook received for flow {FlowId} ('{TriggerKey}') — {Bytes} bytes, scheme={Scheme}, result={Result}.")]
    public static partial void WebhookReceived(ILogger logger, Guid flowId, string triggerKey, long bytes, string scheme, string result);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Webhook signature rejected for flow {FlowId} ('{TriggerKey}'): {Reason}.")]
    public static partial void SignatureRejected(ILogger logger, Guid flowId, string triggerKey, string reason);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Webhook replay rejected for flow {FlowId} ('{TriggerKey}'): {Reason}.")]
    public static partial void ReplayRejected(ILogger logger, Guid flowId, string triggerKey, string reason);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Webhook rate-limited for flow {FlowId} ('{TriggerKey}') from {ClientKey}.")]
    public static partial void RateLimited(ILogger logger, Guid flowId, string triggerKey, string clientKey);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Webhook payload too large for flow {FlowId} ('{TriggerKey}'): {Bytes} bytes (cap={CapBytes}).")]
    public static partial void PayloadTooLarge(ILogger logger, Guid flowId, string triggerKey, long bytes, long capBytes);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning, Message = "Webhook IP denied for flow {FlowId} ('{TriggerKey}') from {ClientIp}.")]
    public static partial void IpDenied(ILogger logger, Guid flowId, string triggerKey, string clientIp);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "Webhook secret invalid for flow {FlowId} ('{TriggerKey}').")]
    public static partial void SecretInvalid(ILogger logger, Guid flowId, string triggerKey);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Information, Message = "Webhook accepted for flow {FlowId} ('{TriggerKey}'), run {RunId}.")]
    public static partial void DeliveryAccepted(ILogger logger, Guid flowId, string triggerKey, Guid runId);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning, Message = "Webhook rejection store write failed for flow {FlowId} ('{TriggerKey}'); rejection still returned.")]
    public static partial void RejectionStoreFailed(ILogger logger, Exception ex, Guid flowId, string triggerKey);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Warning, Message = "Webhook replay store error for flow {FlowId} ('{TriggerKey}'); failing closed.")]
    public static partial void ReplayStoreFailed(ILogger logger, Exception ex, Guid flowId, string triggerKey);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Webhook accepted using rotated secondary key for flow {FlowId} ('{TriggerKey}'); rotate publishers off the previous key soon.")]
    public static partial void RotationUsedPreviousKey(ILogger logger, Guid flowId, string triggerKey);
}
