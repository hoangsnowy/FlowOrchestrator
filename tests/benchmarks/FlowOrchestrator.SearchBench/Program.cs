using System.Globalization;
using FlowOrchestrator.SearchBench;

// PROTOTYPE investigation harness — see README.md. Usage: dotnet run -c Release -- <command> [args]
var command = args.Length > 0 ? args[0] : "help";
int Arg(int i, int fallback) => args.Length > i ? int.Parse(args[i], CultureInfo.InvariantCulture) : fallback;
string SArg(int i, string fallback) => args.Length > i ? args[i] : fallback;

switch (command)
{
    case "stats":
    {
        // Data-shape summary without touching a database.
        var gen = new DataGenerator(Arg(1, 200_000));
        long runs = 0, steps = 0, withOutput = 0, withError = 0, chars = 0, tokensCore = 0, tokensPayload = 0;
        var buckets = new long[6];
        foreach (var run in gen.Generate())
        {
            runs++;
            foreach (var s in run.Steps)
            {
                steps++;
                if (s.ErrorMessage is not null) withError++;
                if (s.OutputJson is null) continue;
                withOutput++;
                chars += s.OutputJson.Length;
                var kb = s.OutputJson.Length / 1024.0;
                buckets[kb < 1 ? 0 : kb < 4 ? 1 : kb < 16 ? 2 : kb < 32 ? 3 : 4]++;
            }
            if (runs % 20 == 0)
            {
                tokensCore += SearchTokenizer.ForRun(run, includePayload: false).Count();
                tokensPayload += SearchTokenizer.ForRun(run, includePayload: true).Count();
            }
        }
        Console.WriteLine($"runs={runs:N0} steps={steps:N0} withOutput={withOutput:N0} withError={withError:N0} ({100.0 * withError / steps:F2}%)");
        Console.WriteLine($"output chars={chars:N0} avg={chars / (double)withOutput:N0} (<1K {buckets[0]:N0}, 1-4K {buckets[1]:N0}, 4-16K {buckets[2]:N0}, 16-32K {buckets[3]:N0}, 32K+ {buckets[4]:N0})");
        Console.WriteLine($"tokens/run (sampled 1 in 20): core={tokensCore * 20.0 / runs:F1} with payload={tokensPayload * 20.0 / runs:F1}");
        foreach (var (k, v) in gen.Needles.Values) Console.WriteLine($"  needle {k} = {v}");
        break;
    }
    case "mssql-seed": await MsSqlSeeder.SeedAsync(Arg(1, 200_000), withEvents: true); break;
    case "mssql-base": await MsSqlBench.BaselineAsync(deep: SArg(1, "quick") == "deep", quick: SArg(1, "quick") == "quick"); break;
    case "mssql-setup": await MsSqlBench.SetupAsync(SArg(1, "")); break;
    case "mssql-tokens": await MsSqlBench.LoadTokensAsync(Arg(1, 200_000), includePayload: SArg(2, "payload") == "payload"); break;
    case "mssql-cand": await MsSqlBench.CandidateAsync(SArg(1, "")); break;
    case "mssql-keyset": await MsSqlBench.KeysetAsync(); break;
    case "mssql-sizes": await MsSqlBench.SizesAsync(); break;
    case "mssql-write": await WriteBench.MsSqlAsync(SArg(1, "baseline"), Arg(2, 200), tokens: SArg(3, "") == "tokens"); break;
    case "mssql-sql":
    {
        // Ad-hoc capture: mssql-sql "<name>" "<sql>"
        Console.WriteLine(await MsSqlBench.CaptureAsync(SArg(1, "adhoc"), SArg(2, "SELECT 1"), null));
        break;
    }
    case "pg-seed": await PgBench.SeedAsync(Arg(1, 200_000), withEvents: true); break;
    case "pg-base": await PgBench.BaselineAsync(deep: SArg(1, "quick") == "deep", quick: SArg(1, "quick") == "quick"); break;
    case "pg-setup": await PgBench.SetupAsync(SArg(1, "")); break;
    case "pg-tokens": await PgBench.LoadTokensAsync(Arg(1, 200_000), includePayload: SArg(2, "payload") == "payload"); break;
    case "pg-cand": await PgBench.CandidateAsync(SArg(1, "")); break;
    case "pg-keyset": await PgBench.KeysetAsync(); break;
    case "pg-sizes": await PgBench.SizesAsync(); break;
    case "pg-write": await WriteBench.PgAsync(SArg(1, "baseline"), Arg(2, 200), tokens: SArg(3, "") == "tokens"); break;
    default:
        Console.WriteLine("commands: stats | mssql-seed|mssql-base [quick|deep]|mssql-setup <c>|mssql-tokens|mssql-cand <c>|mssql-keyset|mssql-sizes|mssql-write <label> <runs> [tokens]");
        Console.WriteLine("          pg-seed|pg-base [quick|deep]|pg-setup <c>|pg-tokens|pg-cand <c>|pg-keyset|pg-sizes|pg-write <label> <runs> [tokens]");
        break;
}
