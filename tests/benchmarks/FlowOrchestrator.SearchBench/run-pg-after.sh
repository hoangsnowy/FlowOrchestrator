#!/usr/bin/env bash
# 2.0 "after" sequence for PostgreSQL: shipped migrator + shipped query text; stored-column candidate for comparison.
# Usage: run-pg-after.sh <results-dir>
set -u
export SEARCHBENCH_OUT=${1:-$PWD/results}
mkdir -p "$SEARCHBENCH_OUT"
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
FROM=${FROM:-}
step() {
  if [ -n "$FROM" ]; then [ "$*" = "$FROM" ] && FROM= || { echo "--- skip $*"; return; }; fi
  echo "=== $(date -u +%H:%M:%S) $*"; $BIN "$@" || echo "!!! failed: $*"
}
if [ -z "$FROM" ]; then
docker rm -f fo-search-pg >/dev/null 2>&1
docker run -d --name fo-search-pg -m 10g --shm-size=1g -e POSTGRES_PASSWORD=searchbench -p 15433:5432 postgres:18-alpine \
  -c shared_buffers=2GB -c effective_cache_size=6GB -c work_mem=64MB -c maintenance_work_mem=1GB -c max_wal_size=8GB
for i in $(seq 1 60); do docker exec fo-search-pg pg_isready -U postgres >/dev/null 2>&1 && break; sleep 2; done
sleep 5
fi
step pg-seed 200000
step pg-write pre-migrate 200
step pg-migrate
step pg-sizes
SB_SUITE=pg-after step pg-base quick
SB_SUITE=pg-after step pg-base deep
step pg-keyset
step pg-write after 200
# Stored generated column (the measured candidate) on the same data, for the expression-vs-column decision.
step pg-setup trgm-run
step pg-cand trgm-run
step pg-write with-stored-column 200
step pg-sizes
