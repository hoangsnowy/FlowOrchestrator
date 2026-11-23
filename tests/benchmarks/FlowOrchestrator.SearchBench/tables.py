"""Builds the markdown comparison tables of docs/benchmarks/run-search-2026-11-23.md from results/timings.tsv.

The LAST measurement of each (suite, scenario) pair wins, so a re-run replaces an earlier sample.
Usage: python tables.py <timings.tsv>
"""
import sys
sys.stdout.reconfigure(encoding="utf-8")

rows = {}
for line in open(sys.argv[1], encoding="utf-8"):
    f = line.rstrip("\n").split("\t")
    if len(f) < 10:
        continue
    _, suite, name, n, p50, p95, mn, mx, cnt = f[:9]
    rows[(suite, name)] = (int(n), float(p50), float(p95), float(mn), float(mx), int(cnt))


def cell(suite, name):
    r = rows.get((suite, name))
    if r is None:
        return "—"
    n, p50, p95, mn, mx, cnt = r
    def ms(v):
        return f"{v/1000:,.1f} s" if v >= 10_000 else f"{v:,.1f}" if v >= 10 else f"{v:,.2f}"
    if n <= 3:
        return f"{ms(mn)} / {ms(p50)} / {ms(mx)} (n=3, {cnt})"
    return f"{ms(p50)} / {ms(p95)} ({cnt})"


def table(title, scenarios, suites):
    print(f"\n#### {title}\n")
    print("| Scenario | " + " | ".join(label for label, _ in suites) + " |")
    print("|---|" + "---|" * len(suites))
    for s in scenarios:
        print(f"| {s} | " + " | ".join(cell(suite, s) for _, suite in suites) + " |")


names = []
for (suite, name) in rows:
    if name not in names:
        names.append(name)

quick = [n for n in names if not n.startswith("deep") and ("[7d]" in n or "[all]" in n)]
plain = [n for n in names if not n.startswith("deep") and n not in quick and not n.startswith("COUNT") and "keyset" not in n
         and not n.startswith("capped") and not n.startswith("dm_") and not n.startswith("reltuples")]
deep = [n for n in names if n.startswith("deep")]

for backend in ("mssql", "pg"):
    if not any(k[0].startswith(backend) for k in rows):
        continue
    if backend == "mssql":
        table("SQL Server — quick search (p50 / p95 ms (rows); n=3 rows: min / median / max)", quick,
              [("shipped", "mssql-base"), ("id-prefix index (UNION shape)", "mssql-idtext"),
               ("narrow BIN2 search column", "mssql-searchcol"), ("token table (run tokens)", "mssql-tokens-quick")])
        table("SQL Server — deep search", deep,
              [("shipped LIKE", "mssql-base"), ("BIN2 LIKE (no schema)", "mssql-collate-deep"),
               ("Full-Text Search", "mssql-fts"), ("token table", "mssql-tokens")])
        table("SQL Server — listing, paging, counts", plain + [n for n in names if "keyset" in n or n.startswith(("COUNT", "capped", "dm_"))],
              [("shipped", "mssql-base"), ("keyset", "mssql-keyset"), ("count", "mssql-count")])
    else:
        table("PostgreSQL — quick search", quick,
              [("shipped", "pg-base"), ("uuid range", "pg-uuid-range"), ("trigram search column", "pg-trgm-run"), ("token table", "pg-tokens-quick")])
        table("PostgreSQL — deep search", deep,
              [("shipped (with output trigram)", "pg-base"), ("shipped, output trigram dropped", "pg-base-no-output-trgm"), ("windowed EXISTS", "pg-trgm-deep-windowed"), ("tsvector", "pg-tsv"), ("token table", "pg-tokens")])
