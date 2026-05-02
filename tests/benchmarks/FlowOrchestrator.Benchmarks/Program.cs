using BenchmarkDotNet.Running;

// Standard BenchmarkDotNet entry point. Pass `--filter "*"` from the command
// line to run every benchmark; `--filter "*StepOutputResolver*"` to scope to
// a single class. Run with `dotnet run -c Release --project tests/benchmarks/...`.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal static partial class Program;
