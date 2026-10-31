namespace FlowOrchestrator.Core.Storage;

/// <summary>
/// Structured run-list query for <see cref="IFlowRunStore.SearchRunsAsync"/>. Every filter is
/// AND-combined; unset filters are ignored.
/// </summary>
/// <remarks>
/// The query is deliberately structured rather than a single free-text string: deciding that a term
/// is a run id or a status name happens once, in the caller, so every provider gets the same index
/// seeks instead of a substring scan. Providers must not reinterpret <see cref="Text"/> as a status
/// or an id on their own.
/// </remarks>
public sealed record RunSearchQuery
{
    /// <summary>Restricts results to a single flow.</summary>
    public Guid? FlowId { get; init; }

    /// <summary>Restricts results to runs whose status equals this canonical token (e.g. <c>"Succeeded"</c>).</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Exact run-id match. Providers compare the key column by equality, which is a primary-key seek;
    /// never a cast-and-substring match.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// Case-insensitive substring matched against the run's own columns: id, flow name, trigger key,
    /// status and background job id. When the text parses as a GUID the id column is compared by
    /// equality instead of a substring match.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="Text"/> is also matched against the run's current step
    /// rows (step key, error message, output JSON). Off by default: it reads every step payload in the
    /// window, so callers should only set it together with a bounded <see cref="StartedFrom"/>.
    /// </summary>
    public bool IncludeStepPayloads { get; init; }

    /// <summary>Inclusive lower bound on the run's start time.</summary>
    public DateTimeOffset? StartedFrom { get; init; }

    /// <summary>Inclusive upper bound on the run's start time.</summary>
    public DateTimeOffset? StartedTo { get; init; }

    /// <summary>Number of leading rows to skip (offset pagination, newest first).</summary>
    public int Skip { get; init; }

    /// <summary>Maximum number of rows to return.</summary>
    public int Take { get; init; } = 50;

    /// <summary>
    /// Requests an exact <see cref="RunSearchPage.TotalCount"/>. Honoured only when neither
    /// <see cref="Text"/> nor <see cref="RunId"/> is set, so the count stays an index-only count over
    /// the plain filters; a text search reports <see cref="RunSearchPage.HasMore"/> instead.
    /// </summary>
    public bool IncludeTotal { get; init; }

    /// <summary><see langword="true"/> when <see cref="Text"/> carries a non-blank term.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary><see langword="true"/> when the store should compute <see cref="RunSearchPage.TotalCount"/>.</summary>
    public bool CountsTotal => IncludeTotal && !HasText && RunId is null;
}

/// <summary>One page of <see cref="IFlowRunStore.SearchRunsAsync"/> results, newest first.</summary>
/// <param name="Runs">Rows for the requested page; at most <see cref="RunSearchQuery.Take"/>.</param>
/// <param name="HasMore">
/// <see langword="true"/> when at least one more matching row exists after this page. Providers learn
/// this by reading <c>Take + 1</c> rows, so it never costs a count.
/// </param>
/// <param name="TotalCount">
/// Exact number of matching rows, or <see langword="null"/> when the query did not ask for it or
/// carried a text / run-id term (see <see cref="RunSearchQuery.CountsTotal"/>).
/// </param>
public sealed record RunSearchPage(IReadOnlyList<FlowRunRecord> Runs, bool HasMore, int? TotalCount);
