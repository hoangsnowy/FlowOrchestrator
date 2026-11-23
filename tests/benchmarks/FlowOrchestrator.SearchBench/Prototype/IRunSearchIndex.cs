// PROTOTYPE — proposed 2.0 seam, compiled here only so the shape is type-checked. Not shipped.
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SearchBench.Prototype;

/// <summary>What a search index can answer, so the dashboard can label the search box honestly.</summary>
[Flags]
public enum RunSearchCapabilities
{
    /// <summary>No capability beyond the store's own run-column match.</summary>
    None = 0,

    /// <summary>Arbitrary infix match (<c>%term%</c>) — pg_trgm, or the LIKE fallback.</summary>
    Substring = 1,

    /// <summary>Token-prefix match (<c>term*</c>) — FTS, tsvector, the token table, external engines.</summary>
    TokenPrefix = 2,

    /// <summary>Step error messages are searchable.</summary>
    StepErrors = 4,

    /// <summary>Step output / trigger payload values are searchable.</summary>
    Payloads = 8,

    /// <summary>Results can be ordered newest-first and windowed by start time without a sort over all hits.</summary>
    OrderedByStart = 16,
}

/// <summary>A text query against the index; filters that the run table answers better stay in <see cref="RunSearchQuery"/>.</summary>
/// <param name="Text">The user's term, already stripped of run-id / status interpretation by the caller.</param>
/// <param name="IncludeStepPayloads">Whether payload / error documents may match (the dashboard's "deep" box).</param>
/// <param name="StartedFrom">Inclusive lower bound on run start, when the caller windows the search.</param>
/// <param name="StartedTo">Inclusive upper bound on run start.</param>
/// <param name="Limit">Maximum number of run ids to return; the caller asks for page size + 1.</param>
public sealed record RunSearchIndexQuery(string Text, bool IncludeStepPayloads, DateTimeOffset? StartedFrom, DateTimeOffset? StartedTo, int Limit);

/// <summary>Run ids matching a text query, newest first, without any run columns.</summary>
/// <param name="Hits">Matching runs, newest first.</param>
/// <param name="Truncated"><see langword="true"/> when the index stopped at <see cref="RunSearchIndexQuery.Limit"/>.</param>
public sealed record RunSearchIndexResult(IReadOnlyList<(Guid RunId, DateTimeOffset StartedAt)> Hits, bool Truncated);

/// <summary>The searchable projection of one run, produced by the engine at write time.</summary>
public sealed record RunSearchDocument(Guid RunId, DateTimeOffset StartedAt, string FlowName, string TriggerKey, string Status, string? BackgroundJobId, string? TriggerDataJson);

/// <summary>The searchable projection of one completed step.</summary>
public sealed record StepSearchDocument(Guid RunId, string StepKey, string Status, string? ErrorMessage, string? OutputJson);

/// <summary>
/// Optional text-search seam for the run list. When registered, <c>IFlowRunStore.SearchRunsAsync</c>
/// resolves a <see cref="RunSearchQuery.Text"/> term through the index (run ids only) and hydrates the
/// page with a primary-key <c>IN</c> lookup; when absent, the store's built-in match runs unchanged.
/// </summary>
/// <remarks>
/// <para>Write calls are best-effort and off the claim path, exactly like event persistence: the engine
/// calls them after the durable step write, swallows failures, and never awaits them under a lock. An
/// index may therefore lag the run table; a search must never be the source of truth for state.</para>
/// <para>Built-in implementations (proposed): <c>SqlServerLikeRunSearchIndex</c> (today's behaviour),
/// <c>SqlServerFullTextRunSearchIndex</c> (opt-in, Azure SQL / FTS-enabled SQL Server),
/// <c>PostgreSqlTrigramRunSearchIndex</c> (default when <c>pg_trgm</c> is available), and the portable
/// <c>TokenTableRunSearchIndex</c>. A host plugs Elasticsearch / Azure AI Search by implementing this
/// interface.</para>
/// </remarks>
public interface IRunSearchIndex
{
    /// <summary>What this index can answer; surfaced by the dashboard next to the search box.</summary>
    RunSearchCapabilities Capabilities { get; }

    /// <summary>Indexes (or re-indexes) the run-level fields. Called after <c>StartRunAsync</c> and on terminal status.</summary>
    /// <param name="run">The run projection.</param>
    /// <param name="cancellationToken">Never the request token: indexing outlives any HTTP request.</param>
    /// <returns>A task that completes when the index accepted the document.</returns>
    ValueTask IndexRunAsync(RunSearchDocument run, CancellationToken cancellationToken);

    /// <summary>Indexes one completed step. Called after <c>RecordStepCompleteAsync</c>; O(tokens of this step), never O(steps in the run).</summary>
    /// <param name="step">The step projection.</param>
    /// <param name="cancellationToken">Never the request token.</param>
    /// <returns>A task that completes when the index accepted the document.</returns>
    ValueTask IndexStepAsync(StepSearchDocument step, CancellationToken cancellationToken);

    /// <summary>Returns matching run ids newest first, at most <see cref="RunSearchIndexQuery.Limit"/>.</summary>
    /// <param name="query">The text query.</param>
    /// <param name="cancellationToken">The request token: a search is a read and may be abandoned.</param>
    /// <returns>The matching run ids.</returns>
    Task<RunSearchIndexResult> SearchAsync(RunSearchIndexQuery query, CancellationToken cancellationToken);

    /// <summary>Removes every document of the given runs (retention sweep).</summary>
    /// <param name="runIds">Runs deleted by retention.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the documents are gone.</returns>
    Task RemoveRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken);
}
