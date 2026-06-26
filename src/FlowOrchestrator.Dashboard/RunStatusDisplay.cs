namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Maps between the canonical run-status tokens persisted by the engine and the
/// display labels the dashboard SPA shows to users. The only divergence today is
/// the <c>Skipped</c> status, surfaced as <c>"Blocked"</c> in the UI.
/// </summary>
internal static class RunStatusDisplay
{
    /// <summary>
    /// Rewrites a free-text run-search term so a display label the user typed resolves to
    /// the canonical status token the store matches against. A term that exactly equals a
    /// known display alias (case-insensitive, e.g. <c>"Blocked"</c>) becomes its canonical
    /// status (<c>"Skipped"</c>); any other value — including <see langword="null"/> or
    /// whitespace — is returned unchanged so ordinary substring search is preserved.
    /// </summary>
    /// <param name="search">The raw search term from the run list / command palette query string.</param>
    /// <returns>The canonical status token when the term is a display alias; otherwise the original term.</returns>
    public static string? ResolveSearchAlias(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return search;
        }

        // "Blocked" is the dashboard's display label for the canonical "Skipped" run status.
        return search.Trim().Equals("Blocked", StringComparison.OrdinalIgnoreCase)
            ? "Skipped"
            : search;
    }
}
