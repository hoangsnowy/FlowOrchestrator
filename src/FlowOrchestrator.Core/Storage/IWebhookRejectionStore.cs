namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Persistence contract for the webhook receive log + DLQ. Stores both
/// rejected (<see cref="WebhookRejectionRecord.IsAccepted"/> false) and
/// accepted entries so the dashboard can render a unified recent-deliveries
/// table with precise reason chips.
/// </summary>
public interface IWebhookRejectionStore
{
    /// <summary>Appends one row to the store.</summary>
    /// <param name="record">Row to persist; <see cref="WebhookRejectionRecord.Id"/> ignored on input.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask WriteAsync(WebhookRejectionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent entries, optionally filtered by flow + reason.
    /// </summary>
    /// <param name="flowId">Restrict to a single flow when supplied.</param>
    /// <param name="reason">Restrict to a single reason chip when supplied (case-insensitive).</param>
    /// <param name="includeAccepted"><see langword="false"/> to return rejections only.</param>
    /// <param name="skip">Pagination skip.</param>
    /// <param name="take">Page size; capped at 500.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<WebhookRejectionRecord>> QueryRecentAsync(
        Guid? flowId,
        string? reason,
        bool includeAccepted,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Returns counts by reason for the last <paramref name="window"/>.
    /// Keys are reason strings, values are occurrence counts.
    /// </summary>
    /// <param name="window">Look-back window (typically 24 h).</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyDictionary<string, long>> CountsByReasonAsync(
        TimeSpan window,
        CancellationToken ct = default);
}
