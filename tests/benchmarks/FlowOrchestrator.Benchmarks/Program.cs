using BenchmarkDotNet.Running;
using FlowOrchestrator.Benchmarks;

// Standard BenchmarkDotNet entry point. Pass `--filter "*"` from the command
// line to run every benchmark; `--filter "*StepOutputResolver*"` to scope to
// a single class. Run with `dotnet run -c Release --project tests/benchmarks/...`.
//
// `--store-reads` instead prints the deterministic store-row counts of the ForEach runs that
// ForEachContinuationBenchmarks times (issue #189 P0-1), without BenchmarkDotNet.
if (args is ["--store-reads"])
{
    await StoreReadReport.WriteAsync(Console.Out);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal static partial class Program;
