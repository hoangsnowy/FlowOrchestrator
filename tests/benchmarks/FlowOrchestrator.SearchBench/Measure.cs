using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.SearchBench;

/// <summary>Latency summary of one measured query.</summary>
public sealed record Timing(string Suite, string Name, int Iterations, double P50, double P95, double Min, double Max, int Rows, string? Note)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Suite,-14} {Name,-58} n={Iterations,3} p50={P50,10:F2} ms p95={P95,10:F2} ms min={Min,9:F2} rows={Rows,4} {Note}");
}

/// <summary>Warm-cache latency measurement with an adaptive iteration budget.</summary>
public static class Measure
{
    public static string OutDir { get; } = Environment.GetEnvironmentVariable("SEARCHBENCH_OUT")
        ?? Path.Combine(AppContext.BaseDirectory, "results");

    /// <summary>
    /// Runs <paramref name="query"/> twice to warm plan cache and buffer pool, then up to
    /// <paramref name="iterations"/> more times, stopping early once <paramref name="budget"/> is spent
    /// (but never below 5 iterations) so a 30 s scan does not take 10 minutes per row.
    /// </summary>
    public static async Task<Timing> RunAsync(string suite, string name, Func<Task<int>> query, int iterations = 20, TimeSpan? budget = null, string? note = null)
    {
        budget ??= TimeSpan.FromSeconds(90);
        var rows = await query();
        var first = Stopwatch.StartNew();
        rows = await query();
        first.Stop();
        var samples = new List<double>(iterations);
        var total = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            rows = await query();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            if (samples.Count >= 5 && total.Elapsed > budget) break;
            if (samples.Count >= 3 && samples.Min() > 20_000) break;   // 20 s+ scans: 3 warm samples
        }
        samples.Sort();
        var timing = new Timing(suite, name, samples.Count, Percentile(samples, 0.50), Percentile(samples, 0.95), samples[0], samples[^1], rows, note);
        Console.WriteLine(timing);
        Directory.CreateDirectory(OutDir);
        await File.AppendAllTextAsync(Path.Combine(OutDir, "timings.tsv"),
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:O}\t{suite}\t{name}\t{timing.Iterations}\t{timing.P50:F2}\t{timing.P95:F2}\t{timing.Min:F2}\t{timing.Max:F2}\t{rows}\t{note}\n"));
        return timing;
    }

    public static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static async Task WriteArtifactAsync(string fileName, string content)
    {
        Directory.CreateDirectory(OutDir);
        await File.WriteAllTextAsync(Path.Combine(OutDir, fileName), content);
    }

    /// <summary>Invokes the store's internal <c>BuildSearchRunsCommand</c>, so the baseline is the shipped SQL verbatim.</summary>
    public static (string Sql, object Parameters, bool CountsTotal) ShippedCommand(Type storeType, RunSearchQuery query)
    {
        var method = storeType.GetMethod("BuildSearchRunsCommand", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{storeType.Name}.BuildSearchRunsCommand not found");
        return ((string, object, bool))method.Invoke(null, [query])!;
    }
}

/// <summary>The run-search scenarios measured on both backends, built from the generated needles.</summary>
public static class Scenarios
{
    public static readonly DateTimeOffset Window7d = DataGenerator.Anchor.AddDays(-7);

    /// <summary>(name, query) pairs exactly as the dashboard would send them after <c>RunSearchTerm</c> parsing.</summary>
    public static IEnumerable<(string Name, RunSearchQuery Query)> Quick(Needles n)
    {
        foreach (var windowed in new[] { true, false })
        {
            var w = windowed ? " [7d]" : " [all]";
            DateTimeOffset? from = windowed ? Window7d : null;
            yield return ("runId prefix, recent" + w, new RunSearchQuery { Text = n["runPrefixRecent"], StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("runId prefix, 60d old" + w, new RunSearchQuery { Text = n["runPrefixOld"], StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("flow name, common 'Invoice'" + w, new RunSearchQuery { Text = "Invoice", StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("flow name, rare 'Gdpr'" + w, new RunSearchQuery { Text = "Gdpr", StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("trigger key, common 'order-created'" + w, new RunSearchQuery { Text = "order-created", StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("trigger key, rare 'backfill'" + w, new RunSearchQuery { Text = "backfill", StartedFrom = from, Take = 50, IncludeTotal = true });
            yield return ("no match 'zq-nomatch-7731'" + w, new RunSearchQuery { Text = "zq-nomatch-7731", StartedFrom = from, Take = 50, IncludeTotal = true });
        }
        // Mapped by RunSearchTerm to a status filter / id seek — never windowed by the dashboard.
        yield return ("status word 'Failed' (+total)", new RunSearchQuery { Status = "Failed", Take = 50, IncludeTotal = true });
        yield return ("status word 'TimedOut' (+total)", new RunSearchQuery { Status = "TimedOut", Take = 50, IncludeTotal = true });
        yield return ("full run id (old)", new RunSearchQuery { RunId = Guid.Parse(n["runIdOld"]), Take = 50, IncludeTotal = true });
        yield return ("unfiltered first page (+total)", new RunSearchQuery { Take = 50, IncludeTotal = true });
        yield return ("unfiltered first page (no total)", new RunSearchQuery { Take = 50 });
        yield return ("unfiltered page 50 OFFSET (+total)", new RunSearchQuery { Skip = 49 * 50, Take = 50, IncludeTotal = true });
        yield return ("unfiltered page 2000 OFFSET", new RunSearchQuery { Skip = 1999 * 50, Take = 50 });
    }

    public static IEnumerable<(string Name, RunSearchQuery Query)> Deep(Needles n)
    {
        foreach (var windowed in new[] { true, false })
        {
            var w = windowed ? " [7d]" : " [all]";
            DateTimeOffset? from = windowed ? Window7d : null;
            yield return ("deep output: invoice, 3d old" + w, new RunSearchQuery { Text = n["invoiceRecent"], IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep output: invoice, 60d old" + w, new RunSearchQuery { Text = n["invoiceOld"], IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep output: orderId, 3d old" + w, new RunSearchQuery { Text = n["orderIdRecent"], IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep output: common 'express'" + w, new RunSearchQuery { Text = "express", IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep error: shipment, 3d old" + w, new RunSearchQuery { Text = n["errorShipmentRecent"], IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep error: 'deadlock victim'" + w, new RunSearchQuery { Text = "deadlock victim", IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
            yield return ("deep no match 'zq-nomatch-7731'" + w, new RunSearchQuery { Text = "zq-nomatch-7731", IncludeStepPayloads = true, StartedFrom = from, Take = 50 });
        }
    }
}
