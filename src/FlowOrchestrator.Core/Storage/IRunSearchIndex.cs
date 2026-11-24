namespace FlowOrchestrator.Core.Storage;

/// <summary>What a run-search index can answer, so callers can describe the search honestly.</summary>
[Flags]
public enum RunSearchCapabilities
{
    /// <summary>No capability beyond the store's own run-column match.</summary>
    None = 0,

    /// <summary>Arbitrary infix match (<c>%term%</c>).</summary>
    Substring = 1,

    /// <summary>Token-prefix match (<c>term*</c>): full-text engines, the token table.</summary>
    TokenPrefix = 2,

    /// <summary>Step error messages are searchable.</summary>
    StepErrors = 4,

    /// <summary>Step output and trigger payload values are searchable.</summary>
    Payloads = 8,

    /// <summary>Results come back newest first and windowed by start time without a sort over all hits.</summary>
    OrderedByStart = 16,
}

/// <summary>A text query against an <see cref="IRunSearchIndex"/>.</summary>
/// <param name="Text">The user's term. Run-id and status interpretation already happened in the caller.</param>
/// <param name="IncludeStepPayloads">Whether step / payload documents may match (the dashboard's "deep" search).</param>
/// <param name="StartedFrom">Inclusive lower bound on the run's start time, when the caller windows the search.</param>
/// <param name="StartedTo">Inclusive upper bound on the run's start time.</param>
/// <param name="Limit">Maximum number of run ids to return; callers ask for the rows they need plus one.</param>
public sealed record RunSearchIndexQuery(string Text, bool IncludeStepPayloads, DateTimeOffset? StartedFrom, DateTimeOffset? StartedTo, int Limit);

/// <summary>One run matched by an <see cref="IRunSearchIndex"/>.</summary>
/// <param name="RunId">The matching run.</param>
/// <param name="StartedAt">The run's start time as indexed.</param>
public readonly record struct RunSearchHit(Guid RunId, DateTimeOffset StartedAt);

/// <summary>Run ids matching a <see cref="RunSearchIndexQuery"/>, newest first, without any run columns.</summary>
/// <param name="Hits">Matching runs, newest first.</param>
/// <param name="Truncated"><see langword="true"/> when the index stopped at <see cref="RunSearchIndexQuery.Limit"/>.</param>
public sealed record RunSearchIndexResult(IReadOnlyList<RunSearchHit> Hits, bool Truncated);

/// <summary>The searchable projection of a run, produced when the run starts.</summary>
/// <param name="RunId">The run.</param>
/// <param name="StartedAt">The run's start time.</param>
/// <param name="FlowName">The flow's name.</param>
/// <param name="TriggerKey">The trigger that started the run.</param>
/// <param name="BackgroundJobId">The runtime's job id, if any.</param>
/// <param name="TriggerDataJson">The trigger body.</param>
public sealed record RunSearchDocument(Guid RunId, DateTimeOffset StartedAt, string? FlowName, string? TriggerKey, string? BackgroundJobId, string? TriggerDataJson);

/// <summary>The searchable projection of one completed step.</summary>
/// <param name="RunId">The run the step belongs to.</param>
/// <param name="StepKey">The step key.</param>
/// <param name="Status">The step's terminal status.</param>
/// <param name="ErrorMessage">The failure reason, if any.</param>
/// <param name="OutputJson">The step output.</param>
public sealed record StepSearchDocument(Guid RunId, string StepKey, string Status, string? ErrorMessage, string? OutputJson);

/// <summary>
/// Optional text-search index for the run list's deep (step payload) search. When one is active, a
/// store resolves a deep <see cref="RunSearchQuery.Text"/> term through the index — run ids only — and
/// reads the matching rows by primary key, instead of scanning step payloads with <c>LIKE</c>.
/// </summary>
/// <remarks>
/// <para><b>Built-in:</b> <c>options.UseRunSearchIndex()</c> enables the token table on SQL Server
/// (<c>SqlServerRunSearchTokenIndex</c>). It is maintained by the store's own write statements and
/// back-filled newest-first in the background. Other providers ignore the option for now.</para>
/// <para><b>Custom:</b> register an implementation as <see cref="IRunSearchIndex"/> (Elasticsearch,
/// Azure AI Search, SQL Server Full-Text Search…). The SQL Server store then calls
/// <see cref="IndexRunAsync"/> / <see cref="IndexStepAsync"/> after its durable writes and
/// <see cref="RemoveRunsAsync"/> from retention, and asks <see cref="SearchAsync"/> for deep searches.</para>
/// <para>Write calls are best-effort and happen after the durable write, with
/// <see cref="CancellationToken.None"/>: a failure is swallowed and never fails the step. An index may
/// therefore lag or miss a document; it must never be the source of truth for run state.</para>
/// </remarks>
public interface IRunSearchIndex
{
    /// <summary>What this index can answer.</summary>
    RunSearchCapabilities Capabilities { get; }

    /// <summary>Indexes the run-level fields. Called once, after the run row is written.</summary>
    /// <param name="run">The run projection.</param>
    /// <param name="cancellationToken">Never a request token: indexing outlives any HTTP request.</param>
    /// <returns>A task that completes when the index accepted the document.</returns>
    ValueTask IndexRunAsync(RunSearchDocument run, CancellationToken cancellationToken);

    /// <summary>
    /// Indexes one completed step. Called after the step's completion is durable; must cost
    /// O(size of this step), never O(steps in the run).
    /// </summary>
    /// <param name="step">The step projection.</param>
    /// <param name="cancellationToken">Never a request token.</param>
    /// <returns>A task that completes when the index accepted the document.</returns>
    ValueTask IndexStepAsync(StepSearchDocument step, CancellationToken cancellationToken);

    /// <summary>Returns matching run ids newest first, at most <see cref="RunSearchIndexQuery.Limit"/>.</summary>
    /// <param name="query">The text query.</param>
    /// <param name="cancellationToken">The request token: a search is a read and may be abandoned.</param>
    /// <returns>The matching run ids.</returns>
    Task<RunSearchIndexResult> SearchAsync(RunSearchIndexQuery query, CancellationToken cancellationToken);

    /// <summary>Removes every document of the given runs (retention).</summary>
    /// <param name="runIds">Runs deleted by retention.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task that completes when the documents are gone.</returns>
    Task RemoveRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken);
}
