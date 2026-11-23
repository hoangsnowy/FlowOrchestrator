#!/usr/bin/env bash
# PostgreSQL candidate / write-cost sequence against an already-seeded fo-search-pg container, ordered so
# each write-cost run differs from the previous one by exactly one index:
#   with-run-trgm (shipped indexes + run search column)  ->  + token insert  ->  - output_json trigram  ->  + tsvector
# Usage: run-pg-candidates.sh <results-dir>
set -u
export SEARCHBENCH_OUT=${1:-$PWD/results}
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
step() { echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"; }
# Repair after the first run: drop rows written by an interrupted write run, build the PK, analyze everything.
step pg-exec "DELETE FROM flow_run_search_tokens WHERE started_at > '2026-11-23T00:00:00Z'"
step pg-exec "ALTER TABLE flow_run_search_tokens ADD PRIMARY KEY (token, started_at, run_id, source)"
step pg-exec "VACUUM ANALYZE flow_run_search_tokens"
step pg-exec "VACUUM ANALYZE flow_runs"
step pg-exec "VACUUM ANALYZE flow_steps"
step pg-sizes
step pg-cand tokens-quick
step pg-cand tokens
step pg-write with-run-trgm-again 200
step pg-write tokens 200 tokens
step pg-setup drop-output-trgm
step pg-write without-output-trgm 200
step pg-setup tsv
step pg-cand tsv
step pg-write with-tsv-no-output-trgm 200
step pg-sizes
echo "=== $(date -u +%H:%M:%S) done"
