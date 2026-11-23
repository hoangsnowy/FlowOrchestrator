#!/usr/bin/env bash
# The benefit side of ix_flow_steps_output_json_trgm: the shipped deep search with that index absent,
# on a compacted flow_steps (the tsvector prototype column dropped and the heap rewritten).
# Usage: run-pg-no-output-trgm.sh <results-dir> [dll]
set -u
export SEARCHBENCH_OUT=${1:-$PWD/results}
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet ${2:-$HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll}"
step() { echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"; }
step pg-exec "DROP INDEX IF EXISTS ix_flow_steps_search_tsv"
step pg-exec "ALTER TABLE flow_steps DROP COLUMN IF EXISTS search_tsv"
step pg-exec "VACUUM FULL flow_steps"
step pg-exec "VACUUM ANALYZE flow_steps"
step pg-sizes
export SB_SUITE=pg-base-no-output-trgm
step pg-base deep
echo "=== $(date -u +%H:%M:%S) done"
