using System.Collections.Frozen;

namespace FlowOrchestrator.Core.Execution;

/// <summary>
/// Which inbound HTTP headers an HTTP trigger (the dashboard's webhook and manual-trigger endpoints)
/// never captures into a run's trigger headers.
/// </summary>
/// <remarks>
/// Trigger headers are persisted with the run, served by run detail and replayed by rerun, so credentials
/// (<c>Authorization</c>, cookies, the legacy <c>X-Webhook-Key</c>) must never enter them; hop-by-hop
/// transport headers describe the connection, not the delivery. The dashboard endpoints and
/// <c>FlowTestHost.TriggerWebhookAsync</c> share this one list so tests see what production stores.
/// </remarks>
public static class TriggerHeaderPolicy
{
    private static readonly FrozenSet<string> Excluded = new[]
    {
        "Authorization", "Cookie", "Set-Cookie",
        "X-Webhook-Key", "Proxy-Authorization",
        "Connection", "Transfer-Encoding", "Upgrade", "Content-Length",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns <see langword="true"/> when <paramref name="headerName"/> must not be captured as a trigger header.</summary>
    /// <param name="headerName">The header name; matched case-insensitively.</param>
    public static bool IsExcluded(string headerName) => Excluded.Contains(headerName);
}
