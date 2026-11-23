#!/usr/bin/env bash
# 2.0 "after" sequence for SQL Server: shipped migrator, shipped query text, shipped token index.
# Usage: run-mssql-after.sh <image> <results-dir>
set -u
IMAGE=${1:-mcr.microsoft.com/mssql/server:2022-latest}
export SEARCHBENCH_OUT=${2:-$PWD/results}
mkdir -p "$SEARCHBENCH_OUT"
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
FROM=${FROM:-}
step() {
  if [ -n "$FROM" ]; then [ "$*" = "$FROM" ] && FROM= || { echo "--- skip $*"; return; }; fi
  echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"
}
if [ -z "$FROM" ]; then
docker rm -f fo-search-mssql >/dev/null 2>&1
docker run -d --name fo-search-mssql -m 10g -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=SearchBench!2026' \
  -e MSSQL_MEMORY_LIMIT_MB=8192 -p 14333:1433 "$IMAGE"
for i in $(seq 1 60); do docker logs fo-search-mssql 2>&1 | grep -q "Recovery is complete" && break; sleep 5; done
sleep 10
fi
step mssql-seed 200000
step mssql-write pre-migrate 200
step mssql-migrate tokens
step mssql-sizes
SB_SUITE=mssql-after step mssql-base quick
SB_SUITE=mssql-after step mssql-base deep
step mssql-keyset
step mssql-write after-no-tokens 200
step mssql-write after-shipped-tokens 200 shipped-tokens
step mssql-backfill 500
SB_SUITE=mssql-after-tokens SB_TOKENS=1 step mssql-base deep
step mssql-sizes
