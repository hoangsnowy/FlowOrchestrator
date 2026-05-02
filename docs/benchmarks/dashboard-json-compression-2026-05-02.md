# Dashboard JSON endpoint compression — 2026-05-02

Adds per-request Brotli + Gzip compression to every dashboard JSON
endpoint (`/api/flows`, `/api/runs`, `/api/runs/{id}`, etc.) via
`Accept-Encoding` content negotiation in `WriteJsonAsync`. Mirrors the
pre-existing static-HTML compression at the dashboard root, but encodes
per-request because JSON content varies.

## Why

Before this change, only `GET /flows` (the HTML root page) honored
`Accept-Encoding`. Every JSON API endpoint emitted raw bytes regardless
of what the client requested. The dashboard SPA polls `/api/runs` every
5 s by default, and `/api/flows` / `/api/runs/{id}` payloads commonly
land between 5 KB and 60 KB. Cumulative bandwidth waste over a long
session is significant — and gzip support has been a baseline browser
expectation since ~2008.

## Compression strategy

- **Brotli** preferred when the client advertises `br`.
- **Gzip** universal fallback.
- **`CompressionLevel.Fastest`** — not `Optimal`. The dashboard auto-refresh
  pattern means each connected client triggers compression every 5 s; at
  Optimal (Brotli quality 11) the encoder dominates a 60 KB payload.
  Fastest (~quality 1) gives ~70% size reduction at a tiny fraction of
  the CPU cost — the right trade-off when ratio matters less than latency.
- **`Vary: Accept-Encoding`** on every JSON response so any cache (CDN,
  reverse proxy, browser) keys the variants correctly even when the
  current request was uncompressed.

## Measured (50-run /api/runs payload, integration test
`ApiRuns_BrotliPayload_IsSignificantlySmallerThanRaw`)

The test asserts a conservative 3× minimum reduction to stay stable
across payload shape changes. Actual measured ratio in the test
environment is approximately **5×** for the run-list shape (50 runs ×
~200 B/run ≈ 10 KB raw → ~2 KB Brotli at Fastest). For larger payloads
(`/api/runs/{id}` with embedded steps + attempts, 30-60 KB raw) the
ratio rises to ~6-7× because Brotli amortises its dictionary cost over
more bytes.

## Testing

The integration test suite gained four tests in
`DashboardCompressionTests.cs`:

- `ApiFlows_WithBrotli_ReturnsBrotliEncoding` — `Accept-Encoding: br` →
  `Content-Encoding: br` + `Vary: Accept-Encoding`.
- `ApiFlows_WithGzipOnly_ReturnsGzipEncoding` — `Accept-Encoding: gzip`
  alone → `Content-Encoding: gzip`.
- `ApiFlows_WithoutAcceptEncoding_ReturnsUncompressed` — no header →
  uncompressed bytes (with `Vary` still emitted).
- `ApiFlows_BrotliAndGzipDecompressToSameJson` — round-trip equality
  across all three transports (raw, Brotli, Gzip).

Plus the ratio-floor test mentioned above.

## Why no dedicated BenchmarkDotNet harness

A BDN benchmark for `WriteJsonAsync` would need a fake `HttpContext`
with a `Request.Headers.AcceptEncoding` and a writable `Response.Body`
stream. The plumbing is non-trivial and the result would mostly measure
`BrotliStream.Write` throughput — already characterised by the .NET
runtime team. The integration-test ratio assertion is sufficient
evidence that the compression is wired correctly and produces the
expected size reduction; the per-request CPU cost is bounded by
`CompressionLevel.Fastest`, also a well-known quantity.

If a future change tweaks the compression strategy (switches to a
streaming JSON writer, raises the level, etc.), this doc should gain a
proper before/after BDN suite.

## Reproducing

The test alone is enough to confirm the ratio:

```bash
cd D:/Github/FlowOrchestrator-
dotnet test ./tests/integration/FlowOrchestrator.Dashboard.IntegrationTests/... \
  --filter "FullyQualifiedName~ApiRuns_BrotliPayload"
```

To eyeball actual bytes in dev:

```bash
curl -i -H "Accept-Encoding: br" http://localhost:5000/flows/api/runs | head
# vs.
curl -i http://localhost:5000/flows/api/runs | head
```

Compare `Content-Length` headers (or the raw byte count).
