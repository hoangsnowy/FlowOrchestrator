#!/usr/bin/env bash
# Full PostgreSQL sequence. Usage: run-pg.sh <results-dir>
set -u
export SEARCHBENCH_OUT=${1:-$PWD/results}
mkdir -p "$SEARCHBENCH_OUT"
HERE=$(cd "$(dirname "$0")" && pwd)
BIN="dotnet $HERE/bin/Release/net11.0/FlowOrchestrator.SearchBench.dll"
# FROM="<command> <arg>" resumes at that step against an already-seeded container (no docker run, no seed).
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
step pg-sizes
step pg-base quick
step pg-keyset
step pg-write baseline-with-output-trgm 200
step pg-base deep
step pg-cand uuid-range
step pg-cand trgm-deep-windowed
step pg-setup trgm-run;  step pg-cand trgm-run
step pg-write with-run-trgm 200
step pg-tokens 200000 payload
step pg-cand tokens-quick
step pg-cand tokens
step pg-write tokens 200 tokens
step pg-setup tsv;       step pg-cand tsv
step pg-write with-tsv 200
step pg-sizes
step pg-setup drop-output-trgm
step pg-write without-output-trgm 200
echo "=== $(date -u +%H:%M:%S) done"
