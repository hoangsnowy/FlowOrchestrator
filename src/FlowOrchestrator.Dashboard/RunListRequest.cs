using System.Globalization;
using FlowOrchestrator.Core.Storage;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Translates the <c>GET /api/runs</c> query string into a structured <see cref="RunSearchQuery"/>:
/// classifies the search term, applies the default start-time window and decides whether a total
/// may be counted.
/// </summary>
/// <param name="Query">The store query to run.</param>
/// <param name="Term">How the search term was interpreted, echoed to the SPA.</param>
/// <param name="WindowDefaulted"><see langword="true"/> when <see cref="FlowDashboardOptions.RunSearchWindow"/> supplied the lower bound.</param>
/// <param name="IncludeTotal">The caller asked for the paged <c>{ items, total, hasMore }</c> shape.</param>
/// <param name="IsPlainListing">No filter of any kind was given: the cheap <see cref="IFlowRunStore.GetRunsAsync"/> listing applies.</param>
internal sealed record RunListRequest(
    RunSearchQuery Query,
    RunSearchTerm Term,
    bool WindowDefaulted,
    bool IncludeTotal,
    bool IsPlainListing)
{
    /// <summary>
    /// Parses the query string.
    /// </summary>
    /// <param name="query">The request query collection.</param>
    /// <param name="options">Dashboard options, for <see cref="FlowDashboardOptions.RunSearchWindow"/>.</param>
    /// <param name="now">Current time, the upper end of a defaulted window.</param>
    /// <returns>The resolved request.</returns>
    /// <remarks>
    /// Query parameters: <c>flowId</c>, <c>status</c>, <c>search</c>, <c>skip</c>, <c>take</c>,
    /// <c>includeTotal</c>, <c>from</c>, <c>to</c>, and <c>deep</c> — <c>deep=true|1</c> opts in to
    /// matching step key / error / output JSON. Step payloads are not searched by default.
    /// </remarks>
    public static RunListRequest Parse(IQueryCollection query, FlowDashboardOptions options, DateTimeOffset now)
    {
        Guid? flowId = query.TryGetValue("flowId", out var flowIdValues) && Guid.TryParse(flowIdValues, out var fid) ? fid : null;
        string? status = query.TryGetValue("status", out var statusValues) ? statusValues.ToString() : null;
        if (string.IsNullOrWhiteSpace(status)) status = null;
        string? search = query.TryGetValue("search", out var searchValues) ? searchValues.ToString() : null;
        int skip = query.TryGetValue("skip", out var skipValues) && int.TryParse(skipValues, out var s) ? Math.Max(0, s) : 0;
        int take = query.TryGetValue("take", out var takeValues) && int.TryParse(takeValues, out var t) ? Math.Clamp(t, 1, 500) : 50;
        bool includeTotal = query.TryGetValue("includeTotal", out var includeTotalValues)
            && bool.TryParse(includeTotalValues, out var includeTotalParsed)
            && includeTotalParsed;
        DateTimeOffset? startedFrom = query.TryGetValue("from", out var fromValues)
            && DateTimeOffset.TryParse(fromValues, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fromParsed)
            ? fromParsed.ToUniversalTime() : null;   // UTC: Npgsql rejects a non-zero offset for timestamptz
        DateTimeOffset? startedTo = query.TryGetValue("to", out var toValues)
            && DateTimeOffset.TryParse(toValues, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var toParsed)
            ? toParsed.ToUniversalTime() : null;
        var deepRaw = query.TryGetValue("deep", out var deepValues) ? deepValues.ToString() : null;
        bool deep = string.Equals(deepRaw, "true", StringComparison.OrdinalIgnoreCase) || deepRaw == "1";

        var term = RunSearchTerm.Parse(search, hasExplicitStatus: status is not null, includeStepPayloads: deep);

        // Free text is a %term% match that no index can serve, so it always runs inside a start-time
        // window; with no lower bound from the caller, the configured one applies (anchored on `to`
        // when only `to` was given). Run-id and status terms are seeks and are not windowed.
        var windowDefaulted = false;
        if (term.Kind == RunSearchTermKind.Text && startedFrom is null)
        {
            startedFrom = (startedTo ?? now) - options.EffectiveRunSearchWindow;
            windowDefaulted = true;
        }

        var storeQuery = new RunSearchQuery
        {
            FlowId = flowId,
            Status = term.Kind == RunSearchTermKind.Status ? term.Status : status,
            RunId = term.RunId,
            Text = term.Text,
            IncludeStepPayloads = deep && term.Kind == RunSearchTermKind.Text,
            StartedFrom = startedFrom,
            StartedTo = startedTo,
            Skip = skip,
            Take = take,
            IncludeTotal = includeTotal,
        };

        var isPlainListing = !includeTotal
            && term.Kind == RunSearchTermKind.None
            && status is null
            && startedFrom is null
            && startedTo is null;

        return new RunListRequest(storeQuery, term, windowDefaulted, includeTotal, isPlainListing);
    }

    /// <summary>
    /// Describes how the search was applied, so the SPA can reflect a status shortcut in its status
    /// filter, explain a synonym, and show the window it searched.
    /// </summary>
    /// <returns>A JSON-serialisable description; <see langword="null"/> when there was no search term.</returns>
    public object? DescribeSearch() => Term.Kind == RunSearchTermKind.None
        ? null
        : new
        {
            kind = Term.Kind switch
            {
                RunSearchTermKind.RunId => "runId",
                RunSearchTermKind.Status => "status",
                _ => "text",
            },
            appliedStatus = Term.Kind == RunSearchTermKind.Status ? Term.Status : null,
            alias = Term.Alias,
            stepPayloads = Query.IncludeStepPayloads,
            from = Query.StartedFrom,
            to = Query.StartedTo,
            windowDefaulted = WindowDefaulted,
        };
}
