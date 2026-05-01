using System.Diagnostics;

namespace FlowOrchestrator.Core.Observability;

/// <summary>
/// Helpers for extracting an <see cref="ActivityContext"/> from W3C trace-context headers on an
/// inbound HTTP request.
/// </summary>
/// <remarks>
/// The Dashboard's webhook and signal endpoints call <see cref="TryParse"/> with the values of the
/// inbound <c>traceparent</c> / <c>tracestate</c> headers and, on success, start the entry-point
/// activity as a child of the parent context. This stitches the run's spans onto the caller's
/// distributed trace even when the consuming app has not enabled
/// <c>AddAspNetCoreInstrumentation()</c>. The helper lives in Core so it is reusable from any
/// runtime adapter that exposes its own ingress points.
/// </remarks>
public static class InboundTraceContext
{
    /// <summary>The standard W3C header name for the incoming trace identifier.</summary>
    public const string TraceparentHeaderName = "traceparent";

    /// <summary>The standard W3C header name for vendor-specific trace state.</summary>
    public const string TracestateHeaderName = "tracestate";

    /// <summary>
    /// Attempts to parse an <see cref="ActivityContext"/> from the standard W3C headers.
    /// </summary>
    /// <param name="traceparent">Value of the <c>traceparent</c> header, or <see langword="null"/>.</param>
    /// <param name="tracestate">Value of the <c>tracestate</c> header, or <see langword="null"/>.</param>
    /// <param name="context">Receives the parsed context on success.</param>
    /// <returns><see langword="true"/> when both <paramref name="traceparent"/> is present and parses successfully.</returns>
    public static bool TryParse(string? traceparent, string? tracestate, out ActivityContext context)
    {
        if (string.IsNullOrEmpty(traceparent))
        {
            context = default;
            return false;
        }

        return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out context);
    }

    /// <summary>
    /// Starts an inbound activity, using the W3C headers as the parent context when present.
    /// Falls back to a normal root activity when the headers are absent or invalid.
    /// </summary>
    /// <param name="source">The activity source to start the activity on.</param>
    /// <param name="name">Activity name (e.g. <c>"flow.webhook.receive"</c>, <c>"flow.signal.deliver"</c>).</param>
    /// <param name="kind">Activity kind. Defaults to <see cref="ActivityKind.Server"/> for HTTP ingress.</param>
    /// <param name="traceparent">Inbound <c>traceparent</c> header value or <see langword="null"/>.</param>
    /// <param name="tracestate">Inbound <c>tracestate</c> header value or <see langword="null"/>.</param>
    /// <returns>The started activity, or <see langword="null"/> when no listener is subscribed.</returns>
    public static Activity? StartActivity(
        ActivitySource source,
        string name,
        ActivityKind kind,
        string? traceparent,
        string? tracestate)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (TryParse(traceparent, tracestate, out var parentContext))
        {
            return source.StartActivity(name, kind, parentContext);
        }

        return source.StartActivity(name, kind);
    }
}
