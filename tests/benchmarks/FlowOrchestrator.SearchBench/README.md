# FlowOrchestrator.SearchBench — run-search investigation harness (PROTOTYPE)

Throwaway-quality but reproducible harness behind
`docs/benchmarks/run-search-2026-11-23.md`. It is **not** in any `.slnx`, not run by CI, and none of the
candidate schemas it creates are shipped by the migrators.

## What it does

1. Generates a deterministic production-shape data set (`DataGenerator`, seed `20261123`):
   200,000 runs over 90 days ending 2026-11-23T00:00Z, 20 flows with Zipf-like volume, 8-12 steps each
   (1.99M `FlowSteps` rows), 4.4 G chars of `OutputJson` (avg 2.8K, 0.4K-64K), 4.4% of steps with an
   error message, and ~4.4M `FlowEvents` rows mirroring the engine's `RecordEventAsync` calls.
   SQL Server and PostgreSQL get byte-identical rows.
2. Measures the shipped `BuildSearchRunsCommand` SQL of both stores (invoked by reflection, so the
   baseline is the real query text) on every scenario in `Scenarios`: p50/p95 over >=20 warm iterations
   (adaptive: a query slower than the budget stops after 5), plus STATISTICS IO + actual-plan operator
   summary (SQL Server) / `EXPLAIN (ANALYZE, BUFFERS)` (PostgreSQL).
3. Applies candidate schemas (`*-setup`) and measures the candidate query shapes (`*-cand`) on the same data.
4. Measures per-step write cost through the real store methods (`*-write`), with and without the
   candidate's extra index / token insert, and the `RecordEventAsync` share.

## Run order (one database container at a time — concurrent SQL Server starts crash)

```bash
docker build -t floworch-mssql-fts:2022 tests/benchmarks/FlowOrchestrator.SearchBench/docker/mssql-fts
docker run -d --name fo-search-mssql -m 10g -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='SearchBench!2026' \
  -e MSSQL_MEMORY_LIMIT_MB=8192 -p 14333:1433 floworch-mssql-fts:2022

dotnet build tests/benchmarks/FlowOrchestrator.SearchBench -c Release
cd tests/benchmarks/FlowOrchestrator.SearchBench/bin/Release/net11.0
export SEARCHBENCH_OUT=<results dir>
dotnet FlowOrchestrator.SearchBench.dll mssql-seed 200000
dotnet FlowOrchestrator.SearchBench.dll mssql-base quick
dotnet FlowOrchestrator.SearchBench.dll mssql-base deep
dotnet FlowOrchestrator.SearchBench.dll mssql-keyset
dotnet FlowOrchestrator.SearchBench.dll mssql-write baseline 200
dotnet FlowOrchestrator.SearchBench.dll mssql-setup idtext    && dotnet FlowOrchestrator.SearchBench.dll mssql-cand idtext
dotnet FlowOrchestrator.SearchBench.dll mssql-setup searchcol && dotnet FlowOrchestrator.SearchBench.dll mssql-cand searchcol
dotnet FlowOrchestrator.SearchBench.dll mssql-cand collate-deep
dotnet FlowOrchestrator.SearchBench.dll mssql-tokens 200000 payload && dotnet FlowOrchestrator.SearchBench.dll mssql-cand tokens
dotnet FlowOrchestrator.SearchBench.dll mssql-write tokens 200 tokens
dotnet FlowOrchestrator.SearchBench.dll mssql-setup fts       && dotnet FlowOrchestrator.SearchBench.dll mssql-cand fts
dotnet FlowOrchestrator.SearchBench.dll mssql-write fts 200
dotnet FlowOrchestrator.SearchBench.dll mssql-sizes
docker rm -f fo-search-mssql

docker run -d --name fo-search-pg -m 10g -e POSTGRES_PASSWORD=searchbench -p 15433:5432 postgres:18-alpine \
  -c shared_buffers=2GB -c effective_cache_size=6GB -c work_mem=64MB -c max_wal_size=8GB
dotnet FlowOrchestrator.SearchBench.dll pg-seed 200000
dotnet FlowOrchestrator.SearchBench.dll pg-base quick
dotnet FlowOrchestrator.SearchBench.dll pg-base deep
# ...pg-keyset, pg-cand uuid-range, pg-setup trgm-run + pg-cand trgm-run, pg-cand trgm-deep-windowed,
#    pg-setup tsv + pg-cand tsv, pg-tokens + pg-cand tokens, pg-write <label> 200 [tokens], pg-sizes
docker rm -f fo-search-pg
```

`stats` prints the data-shape summary and the needles without touching a database.
