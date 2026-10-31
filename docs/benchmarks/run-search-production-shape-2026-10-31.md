# RUN search on the production shape — before / after, 2026-10-31

The dashboard run search took about two minutes on a production Azure SQL database
(insurance-integration, FlowOrchestrator 1.33.0, measured 2026-10-27): 15,615 runs
(`Skipped` 11,393 / `Succeeded` 3,971 / `Failed` 251), 35,452 step rows, ~54 MB of
`FlowSteps.OutputJson` (the `fetchJobDetails` step averages 12 KB). Every index existed.
The query shape was the problem:

1. `@SearchLike = '%term%'` — a leading wildcard rules out every index.
2. `COUNT(*) OVER()` evaluated the predicate over every run in retention, not one page.
3. The dashboard default was deep search, so any run that missed the run columns paid a
   case-insensitive `LIKE` over its step payloads: every `OutputJson` LOB page was read.
   Users type `Completed`, which is not a run status, so *every* run fell through.
4. `CAST(fr.Id AS NVARCHAR(36)) LIKE` ran even for a full GUID.

## What changed

| Before | After |
|---|---|
| Free text matched run columns **and** step key / error / output JSON | Run columns only; step payloads behind an opt-in checkbox (`deep=true`) |
| No window | Text searches default to the last 7 days (`FlowDashboardOptions.RunSearchWindow`) |
| `COUNT(*) OVER()` on every page | `Take + 1` rows → `hasMore`; exact `COUNT(*)` only for plain filters, as a separate index-only statement |
| Full GUID → `CAST(Id) LIKE '%guid%'` | `fr.Id = @RunId` |
| `Completed` → substring scan | Mapped to `Status = 'Succeeded'`, explained in the UI |

Why `Take + 1` rather than a capped count (`SELECT COUNT(*) FROM (SELECT TOP (@Cap) …)`): a capped
count still evaluates the `%term%` predicate until it finds `Cap` matches, so a rare term reads the
whole window twice (once for the page, once for the count). `Take + 1` stops after one row past the
page, and the pager only ever needs "is there a next page".

## Measurement

`tests/integration/FlowOrchestrator.SqlServer.IntegrationTests/SqlRunSearchProductionShapeTests.cs`
— SQL Server 2022 (Testcontainers), real migrator and indexes, seeded with the production shape:
15,000 runs spread over 60 days, 73% Skipped / 25% Succeeded / 2% Failed, three steps per run,
~12 KB (6,000-char) `OutputJson` on the first step — above the 8,000-byte in-row limit, so it lives on
LOB pages as in production. "Before" is the 1.33.0 query text run against the same container.

The test asserts on `SET STATISTICS IO` logical and LOB reads, which are deterministic for a given
plan and data set. Elapsed time is printed but not asserted (an upper bound on wall-clock is the flake
pattern CLAUDE.md forbids); it is a warm local container, so the absolute milliseconds understate the
production gap — the Azure SQL tier reads those 60,000 LOB pages from storage.

| Search | Before: ms / logical / LOB | After: ms / logical / LOB | Tables after |
|---|---|---|---|
| `Completed` (→ `Status = 'Succeeded'`, with exact total) | 588 / 48,248 / 60,000 | 1.6 / 212 / 0 | FlowRuns |
| full run id | 584 / 48,252 / 59,996 | 0.8 / 3 / 0 | FlowRuns |
| TriggerKey fragment `claim-0001` (rare; scans the whole 7-day window) | 604 / 48,252 / 59,996 | 8.0 / 44 / 0 | FlowRuns |
| TriggerKey fragment `webhook-claim` (1,500 matches, **no** window) | 544 / 46,751 / 54,000 | 3.0 / 171 / 0 | FlowRuns |
| opt-in step payload search, 7-day window | 598 / 48,255 / 59,997 | 492 / 11,173 / 7,001 | FlowSteps, FlowRuns |

The `webhook-claim` row is the `COUNT(*) OVER()` guard: without a window and with many matches, only
`Take + 1` lets the scan stop after the 51st match. Every "after" query is also asserted to need no
`Worktable` (the spool a window aggregate builds). Reverting the store to `COUNT(*) OVER()` turns four
of the five tests red.

The opt-in payload search is still a payload scan — that is what it is for — but bounded to the
window: 7,001 of 60,000 LOB pages, and it stops once `Take + 1` runs match.

## SQL generated for the three searches

`Completed` (the dashboard maps it to the status filter and keeps an exact total):

```sql
SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId,
       fr.StartedAt, fr.CompletedAt, fr.SourceRunId
FROM FlowRuns AS fr
WHERE fr.Status = @Status
ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY;
SELECT COUNT(*) FROM FlowRuns AS fr WHERE fr.Status = @Status;
```

Full run id:

```sql
SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId,
       fr.StartedAt, fr.CompletedAt, fr.SourceRunId
FROM FlowRuns AS fr
WHERE fr.Id = @RunId
ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY;
```

TriggerKey fragment (default window):

```sql
SELECT fr.Id, fr.FlowId, fr.FlowName, fr.Status, fr.TriggerKey, fr.TriggerDataJson, fr.BackgroundJobId,
       fr.StartedAt, fr.CompletedAt, fr.SourceRunId
FROM FlowRuns AS fr
WHERE fr.StartedAt >= @StartedFrom AND (
        CAST(fr.Id AS NVARCHAR(36)) LIKE @SearchLike ESCAPE '\'
        OR ISNULL(fr.FlowName, '') LIKE @SearchLike ESCAPE '\'
        OR ISNULL(fr.TriggerKey, '') LIKE @SearchLike ESCAPE '\'
        OR ISNULL(fr.Status, '') LIKE @SearchLike ESCAPE '\'
        OR ISNULL(fr.BackgroundJobId, '') LIKE @SearchLike ESCAPE '\'
    )
ORDER BY fr.StartedAt DESC OFFSET @Skip ROWS FETCH NEXT @TakePlusOne ROWS ONLY;
```

Every column in that predicate is in `IX_FlowRuns_StartedAt` (key `StartedAt DESC`, `INCLUDE (FlowId,
FlowName, Status, TriggerKey, BackgroundJobId, CompletedAt)`, `Id` as the clustering key), so the
search is a range seek over the window with a residual filter, plus key lookups for
`TriggerDataJson` / `SourceRunId` on the returned rows only. A text term that is a full GUID replaces
the `CAST … LIKE` line with `fr.Id = @TextId`.

## Not done, on purpose

- **No nonclustered index on the `LIKE` columns.** A leading wildcard cannot seek it.
- **No full-text index.** After the changes above, the default search reads 44–212 pages; the only
  remaining payload scan is the explicit opt-in, already bounded by the window.

## Webhook delivery log

`WebhookRejections` keeps every delivery (accepted ones too) with no retention, and its search had the
same shape: `[Reason] LIKE @Search OR [TriggerKey] LIKE @Search OR [RemoteIp] LIKE @Search` plus a
`COUNT_BIG(*)` over the whole table. A search now gets the same default window
(`[ReceivedAt] >= @ReceivedFrom`, `IX_WebhookRejections_ReceivedAt`) and `Take + 1` instead of the
count. The columns are short `NVARCHAR`s, not payloads, so no opt-in tier was needed.

## Reproduce

```bash
dotnet test tests/integration/FlowOrchestrator.SqlServer.IntegrationTests -f net10.0 \
  --filter "FullyQualifiedName~ProductionShape" --logger "console;verbosity=detailed"
```

Docker required; seeding takes ~20 s. The suite is compiled for net10.0 only — the query shape does
not depend on the TFM.
