# Dashboard access filter and viewer redaction — 2026-10-31

The 1.34.0 dashboard sign-in adds an access filter to **every** dashboard request (including the 5 s
poll and the event stream), masks secret-looking values for viewers on polled endpoints, and widens the
engine's per-step log scope. This records what each costs, before and after the optimisation pass in the
same release.

BenchmarkDotNet, in-process, .NET 10, `[MemoryDiagnoser]`, 3 warm-ups + 8 iterations. The harness lived in
a scratch project referencing `src/` (not part of the solution).

## Access filter (per dashboard request)

| Case | First version | After |
|---|---|---|
| No filter at all (1.33 unprotected baseline) | 144 ns / 1.24 KB | — |
| Unprotected, sync endpoint | 236 ns / 1.50 KB | **141 ns / 1.24 KB** |
| Unprotected, async endpoint | 1,268 ns / 1.92 KB | **966 ns / 1.46 KB** (baseline 949 ns / 1.45 KB) |
| Basic Auth | 479 ns / 1.93 KB | 420 ns / 1.93 KB (credential check alone 314 ns / 1.6 KB, unchanged from 1.33) |
| OIDC viewer, incl. host authentication + Keycloak claims transform | 2,403 ns / 7.92 KB | 2,106 ns / 7.92 KB (host authentication alone: 1,662 ns / 7.08 KB) |

The filter now returns the endpoint's `ValueTask` directly on the unprotected / Basic paths (no async state
machine) and does not write `HttpContext.Items` when unprotected, so an unprotected dashboard pays nothing
over 1.33.

## Viewer redaction (polled endpoints)

| Case | First version | After |
|---|---|---|
| `/api/flows` manifest, cache hit, same string instance | 3,044 ns / 96 B | **13 ns / 96 B** |
| `/api/flows` manifest, cache hit, equal string from a fresh read | 3,273 ns / 96 B | **118 ns / 96 B** |
| Manifest cache miss (`RedactJson`, ~5 KB) | 42,272 ns / 87 KB | 35,053 ns / 87 KB (once per manifest change) |
| `RedactJson`, ~1 KB step output with no secret-looking name | 6,420 ns / 12.5 KB | **166 ns / 0 B** |
| Run detail for a viewer, 100 steps | 1.41 ms / 2.74 MB | **44.8 µs / 43 KB** |
| Run list, 50 rows (no trigger headers) | 894 ns / 8.9 KB | 872 ns / 8.9 KB |

Changes: the manifest cache is keyed on `(flow id, length)` and confirmed with an ordinal compare instead of
hashing the whole manifest on every poll; `RedactJson` scans the raw text first (`SearchValues<string>` on
.NET 9+, an `IndexOf` loop on .NET 8) and only parses when a secret-looking name — or a `\u` escape that could
spell one — is present; run-list rows drop the trigger body for viewers instead of parsing it.

## Engine log scope (per step)

| Case | 1.33 | 1.34 |
|---|---|---|
| `EngineLogScope.Begin`, NullLogger | 47.9 ns / 392 B | 70.1 ns / 392 B |
| `EngineLogScope.Begin`, scope-aware provider | 65.0 ns / 512 B | 95.7 ns / 512 B |

+22–31 ns and 0 extra bytes per step for `FlowName` / `StepType` / `TriggerKey` (capacity 4 and 7 both round
to 7 buckets; `GetType().Name` is cached, 1.7 ns). O(1) per step.

## Found, not changed in this release (existing on 1.33)

| Where | Cost | Note |
|---|---|---|
| `RunTerminationClassifier` (`IsLeaf` per status, each scanning all statuses) | 1.7 ms / 1.4 MB at 100 iterations, 44.7 ms / 35 MB at 500 | O(n²), once per run completion and per recovered run |
| `FlowGraphPlanner.KeyResolution` rebuilds the known-key `SortedSet` | 39 µs / 85 KB at 100, 275 µs / 417 KB at 500 | per step completion in loop flows |
| `FlowOrchestratorEngine.Continuation` fetches all dispatched keys while a loop runs | not measured | point lookups would do |
| `InMemoryFlowRunStore` search formats `run.Id` per run | ~1 MB per search at 10k runs | dev store |

Tracked on #189.
