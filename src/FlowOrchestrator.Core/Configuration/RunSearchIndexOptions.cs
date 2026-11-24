using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// Options for the built-in run-search token index, enabled with
/// <see cref="FlowOrchestratorBuilder.UseRunSearchIndex"/>. <b>Experimental</b> in 2.0.
/// </summary>
/// <remarks>
/// <para>The token index answers deep search ("find the run whose step output contains
/// <c>INV-001353358</c>") with an index seek instead of a <c>LIKE</c> scan over step payloads: measured
/// on SQL Server at 200k runs / 2M steps, 8 ms against 1.2 s (7-day window) and 84 ms against 98 s (no
/// window).</para>
/// <para>Costs, measured there: about 72 token rows per run (1.3 GB per 200k runs, ~12% of the step
/// table), an 8-minute newest-first background back-fill per 200k existing runs, and <b>one extra commit
/// per step completion and per run start</b> (+5.4 ms p50 on a commit-bound disk, roughly doubling
/// <c>RecordStepCompleteAsync</c>); see <c>docs/benchmarks/run-search-2026-11-23.md</c>.</para>
/// <para>Semantics differ from the default deep search: payload and error text match by <b>token
/// prefix</b>, not substring (see <see cref="RunSearchTokenizer"/>). The run's own columns still match
/// as substrings.</para>
/// <para>Supported by the SQL Server provider (database compatibility level 130 or later). Other
/// providers ignore it.</para>
/// </remarks>
public sealed class RunSearchIndexOptions
{
    /// <summary>Whether the token index is maintained and used. Set by <see cref="FlowOrchestratorBuilder.UseRunSearchIndex"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether existing runs are back-filled into the index in the background. Default <see langword="true"/>.</summary>
    /// <remarks>
    /// While the back-fill is incomplete, deep search still finds the not-yet-indexed (older) runs with
    /// the default <c>LIKE</c> scan over that part of the window, so results stay complete — only slower.
    /// </remarks>
    public bool Backfill { get; set; } = true;

    /// <summary>Runs per back-fill batch (one transaction each). Default 200.</summary>
    public int BackfillBatchSize { get; set; } = 200;

    /// <summary>Pause between back-fill batches, to leave the database headroom. Default 100 ms.</summary>
    public TimeSpan BackfillBatchDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How often tokens of runs deleted by retention are swept. Default 24 hours. The sweep walks the
    /// whole token table once (keyset over its clustered key); orphaned tokens are never returned by a
    /// search, they only take space until then.
    /// </summary>
    public TimeSpan OrphanSweepInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Cap on distinct tokens taken from one run start or step completion. Default <see cref="RunSearchTokenizer.DefaultMaxTokensPerDocument"/>.</summary>
    public int MaxTokensPerDocument { get; set; } = RunSearchTokenizer.DefaultMaxTokensPerDocument;
}
