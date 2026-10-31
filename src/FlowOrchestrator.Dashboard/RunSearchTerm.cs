using System.Collections.Frozen;

namespace FlowOrchestrator.Dashboard;

/// <summary>How the dashboard run search interprets the text a user typed.</summary>
internal enum RunSearchTermKind
{
    /// <summary>No term: list runs by the plain filters only.</summary>
    None,

    /// <summary>The term is a full run id; looked up by primary key.</summary>
    RunId,

    /// <summary>The term names a run status; applied as the status filter instead of a text match.</summary>
    Status,

    /// <summary>Free text, matched as a substring.</summary>
    Text,
}

/// <summary>
/// The classified run-search term. Built by <see cref="Parse"/> before any store is called, so an
/// exact run id or status name becomes an index seek and never a substring scan.
/// </summary>
/// <param name="Kind">How the term is applied.</param>
/// <param name="Text">The substring to match, when <see cref="Kind"/> is <see cref="RunSearchTermKind.Text"/>.</param>
/// <param name="RunId">The run id, when <see cref="Kind"/> is <see cref="RunSearchTermKind.RunId"/>.</param>
/// <param name="Status">The canonical status token, when <see cref="Kind"/> is <see cref="RunSearchTermKind.Status"/>.</param>
/// <param name="Alias">
/// The word the user typed when it is a synonym rather than a status or its display label
/// (e.g. <c>"Completed"</c> for <c>Succeeded</c>), so the UI can say which status it applied.
/// </param>
internal readonly record struct RunSearchTerm(
    RunSearchTermKind Kind,
    string? Text = null,
    Guid? RunId = null,
    string? Status = null,
    string? Alias = null)
{
    /// <summary>
    /// Canonical run statuses plus the dashboard's display labels for them. An exact,
    /// case-insensitive match is applied as the status filter without a notice.
    /// </summary>
    private static readonly FrozenDictionary<string, string> StatusNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Running"] = "Running",
        ["Succeeded"] = "Succeeded",
        ["Failed"] = "Failed",
        ["Skipped"] = "Skipped",
        ["Cancelled"] = "Cancelled",
        ["TimedOut"] = "TimedOut",
        // Display labels the run list shows in place of the canonical token.
        ["Blocked"] = "Skipped",
        ["Timed Out"] = "TimedOut",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Words people type for a status that is not a status. Mapped, and reported back through
    /// <see cref="Alias"/>, instead of falling through to a substring search that matches nothing.
    /// Deliberately excludes words that are as likely to be free text as a status (e.g. "error").
    /// </summary>
    private static readonly FrozenDictionary<string, string> StatusSynonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Completed"] = "Succeeded",
        ["Complete"] = "Succeeded",
        ["Success"] = "Succeeded",
        ["Successful"] = "Succeeded",
        ["Succeed"] = "Succeeded",
        ["Done"] = "Succeeded",
        ["Passed"] = "Succeeded",
        ["Failure"] = "Failed",
        ["Fail"] = "Failed",
        ["Errored"] = "Failed",
        ["Skip"] = "Skipped",
        ["Canceled"] = "Cancelled",
        ["Cancel"] = "Cancelled",
        ["Aborted"] = "Cancelled",
        ["Timeout"] = "TimedOut",
        ["Timed-out"] = "TimedOut",
        ["Time out"] = "TimedOut",
        ["In progress"] = "Running",
        ["In-progress"] = "Running",
        ["Active"] = "Running",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies a raw run-search term.
    /// </summary>
    /// <param name="raw">The text from the run search box or the <c>search</c> query parameter.</param>
    /// <param name="hasExplicitStatus">
    /// <see langword="true"/> when the caller already set a status filter. The explicit filter wins:
    /// a status-like term is then searched as text inside it.
    /// </param>
    /// <param name="includeStepPayloads">
    /// <see langword="true"/> when step payloads are searched too. A GUID is then kept as text, since
    /// the point of a payload search is finding ids such as an order id inside step output; the run's
    /// own id is still compared by equality.
    /// </param>
    /// <returns>The classified term; <see cref="RunSearchTermKind.None"/> for a blank term.</returns>
    /// <remarks>
    /// Wrapping the term in double quotes (<c>"Completed"</c>) skips every shortcut and searches the
    /// quoted text literally.
    /// </remarks>
    public static RunSearchTerm Parse(string? raw, bool hasExplicitStatus, bool includeStepPayloads)
    {
        var term = raw.AsSpan().Trim();
        if (term.IsEmpty)
            return new RunSearchTerm(RunSearchTermKind.None);

        if (term.Length >= 2 && term[0] == '"' && term[^1] == '"')
        {
            var quoted = term[1..^1].Trim();
            return quoted.IsEmpty
                ? new RunSearchTerm(RunSearchTermKind.None)
                : new RunSearchTerm(RunSearchTermKind.Text, Text: quoted.ToString());
        }

        if (!includeStepPayloads && Guid.TryParse(term, out var runId))
            return new RunSearchTerm(RunSearchTermKind.RunId, RunId: runId);

        var text = term.ToString();
        if (!hasExplicitStatus)
        {
            if (StatusNames.TryGetValue(text, out var status))
                return new RunSearchTerm(RunSearchTermKind.Status, Status: status);
            if (StatusSynonyms.TryGetValue(text, out var mapped))
                return new RunSearchTerm(RunSearchTermKind.Status, Status: mapped, Alias: text);
        }

        return new RunSearchTerm(RunSearchTermKind.Text, Text: text);
    }
}
