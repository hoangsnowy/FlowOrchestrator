namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Filter + pagination shape for <see cref="IWebhookRejectionStore.QueryAsync"/>.
/// All fields are optional; defaults return the most recent 50 rows including
/// accepted entries.
/// </summary>
/// <param name="FlowId">Restrict to a single flow when supplied.</param>
/// <param name="Reason">Restrict to a single reason chip when supplied (case-insensitive).</param>
/// <param name="Search">
/// Free-text contains-match against <c>Reason</c>, <c>TriggerKey</c>, and
/// <c>RemoteIp</c>. Case-insensitive. Pass <see langword="null"/> or empty
/// to skip the filter.
/// </param>
/// <param name="IncludeAccepted"><see langword="false"/> to exclude accepted rows (rejected only).</param>
/// <param name="IncludeRejected"><see langword="false"/> to exclude rejected rows (accepted only). Both <see langword="true"/> = no filter.</param>
/// <param name="Skip">Pagination skip (rows).</param>
/// <param name="Take">Page size; capped at 500 by stores.</param>
public sealed record WebhookRejectionQuery(
    Guid? FlowId = null,
    string? Reason = null,
    string? Search = null,
    bool IncludeAccepted = true,
    bool IncludeRejected = true,
    int Skip = 0,
    int Take = 50);

/// <summary>Paged result from <see cref="IWebhookRejectionStore.QueryAsync"/>.</summary>
/// <param name="Items">Rows for the current page (already filtered + paged).</param>
/// <param name="Total">Total row count matching the filter, before pagination.</param>
public sealed record WebhookRejectionPage(
    IReadOnlyList<WebhookRejectionRecord> Items,
    int Total);
