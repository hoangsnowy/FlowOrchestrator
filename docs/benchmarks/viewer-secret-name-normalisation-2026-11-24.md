# Viewer redaction — normalised secret names (2026-11-24)

`ManifestSecretRedactor` now normalises a property / header name before matching it against the secret-name
fragments (case ignored; `-`, `_`, `.` and spaces dropped), so `X-Api-Key`, `api_key` and `apiKey` are the
same name. The cheap text pre-check in `RedactJson` must stay at least as permissive as the per-name check,
so when the raw text has no fragment but does contain a separator it also searches the separator-free text
(copied into a pooled buffer). This records the cost; it applies to read-only callers only — operators and
unprotected Development dashboards never reach the redactor.

BenchmarkDotNet 0.15.8, `tests/benchmarks/FlowOrchestrator.Benchmarks/SecretRedactionBenchmarks.cs`,
`--inProcess` on .NET 11.0 (x64 RyuJIT, Intel Core Ultra 7 255H), `[MemoryDiagnoser]`. "Before" is the same
benchmark compiled against the previous `ManifestSecretRedactor.cs`.

| Case | Before | After |
|---|---|---|
| `RedactJson`, ~1 KB, GUIDs / dates / decimals, no secret-looking name | 98.8 ns / 0 B | 870 ns / 0 B |
| `RedactJson`, ~1 KB, no separators, no secret-looking name | 74.1 ns / 0 B | 92.3 ns / 0 B |
| `IsSecretName("Ocp-Apim-Subscription-Key")` | 62.2 ns / 0 B (and answered *not secret*) | 56.0 ns / 0 B |
| `IsSecretName("orderId")` | 53.4 ns / 0 B | **14.3 ns / 0 B** |
| `MaskHeaders`, 10 headers | 579 ns / 496 B | 618 ns / 496 B |

Reading the numbers:

- The one real increase is a payload that has no secret-looking name but does contain separators — which is
  most payloads (every GUID and date has a `-`). The extra pass is a vectorised copy-and-search, allocation
  free; per 1 KB it is under a microsecond, so a viewer's run detail with 100 steps (two payloads each) pays
  roughly 0.15 ms more per poll. Scale is unchanged: linear in payload size, no allocation.
- `IsSecretName` got faster: one `SearchValues<string>` search of the normalised name instead of a
  `Contains` per fragment, and no copy for a name without separators.
- Run-list rows are unaffected (viewers get no trigger body, and headers go through `MaskHeaders`); flow
  manifests are cached per content, so the catalog poll is unaffected too.

The pre-check was not narrowed further (for example to property names only): every property name appears
verbatim in the text, so searching the separator-free text is provably never stricter than the per-name
check, and a leak is the failure that matters here.
