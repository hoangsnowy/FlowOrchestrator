#!/usr/bin/env bash
# Full SQL Server sequence. Usage: run-mssql.sh <image> <results-dir>
set -u
IMAGE=${1:-floworch-mssql-fts:2022}
export SEARCHBENCH_OUT=${2:-$PWD/results}
mkdir -p "$SEARCHBENCH_OUT"
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
step() { echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"; }
docker rm -f fo-search-mssql >/dev/null 2>&1
docker run -d --name fo-search-mssql -m 10g -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=SearchBench!2026' \
  -e MSSQL_MEMORY_LIMIT_MB=8192 -p 14333:1433 "$IMAGE"
for i in $(seq 1 60); do docker logs fo-search-mssql 2>&1 | grep -q "Recovery is complete" && break; sleep 5; done
sleep 10
step mssql-seed 200000
step mssql-sizes
step mssql-base quick
step mssql-keyset
step mssql-write baseline 200
step mssql-base deep
step mssql-setup idtext;    step mssql-cand idtext
step mssql-setup searchcol; step mssql-cand searchcol
step mssql-tokens 200000 payload
step mssql-cand tokens-quick
step mssql-cand tokens
step mssql-write tokens 200 tokens
step mssql-cand collate-deep
if [ "${WITH_FTS:-1}" = "1" ]; then
  step mssql-setup fts
  step mssql-cand fts
  step mssql-write fts 200
fi
step mssql-sizes
echo "=== $(date -u +%H:%M:%S) done"
