#!/usr/bin/env bash
# Candidate-only SQL Server sequence against an already-seeded fo-search-mssql container, ordered by
# decision value (token table, FTS) so a time-boxed session still gets the numbers that matter.
# Usage: run-mssql-candidates.sh <results-dir>
set -u
export SEARCHBENCH_OUT=${1:-$PWD/results}
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
step() { echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"; }
step mssql-tokens 200000 payload
step mssql-sizes
step mssql-cand tokens-quick
step mssql-cand tokens
step mssql-write tokens 200 tokens
step mssql-setup searchcol
step mssql-cand searchcol
step mssql-setup fts
step mssql-sizes
step mssql-cand fts
step mssql-write fts 200
step mssql-cand collate-deep
echo "=== $(date -u +%H:%M:%S) done"
