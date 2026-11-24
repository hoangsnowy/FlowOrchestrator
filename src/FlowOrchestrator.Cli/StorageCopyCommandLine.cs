using System.Globalization;
using FlowOrchestrator.StorageMigration;

namespace FlowOrchestrator.Cli;

/// <summary>The parsed <c>flow-orchestrator storage copy</c> command line.</summary>
internal sealed class StorageCopyCommand
{
    /// <summary>Options for the copier.</summary>
    public required FlowStorageCopyOptions Options { get; init; }

    /// <summary>Only compare the two sides (<c>--verify-only</c>).</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Print every batch, plus the migrators' log (<c>--verbose</c>).</summary>
    public bool Verbose { get; init; }

    /// <summary>Print only errors and the final summary (<c>--quiet</c>).</summary>
    public bool Quiet { get; init; }
}

/// <summary>The outcome of parsing: a command to run, a help / version request, or a usage error.</summary>
internal sealed class ParseResult
{
    /// <summary>The command, when parsing succeeded and a copy should run.</summary>
    public StorageCopyCommand? Command { get; init; }

    /// <summary>Text to print and exit 0 (help or version).</summary>
    public string? Output { get; init; }

    /// <summary>A usage error to print and exit 2.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Parses <c>flow-orchestrator storage copy ...</c>. Hand-rolled on purpose: one verb, a dozen options, no
/// dependency to version alongside the libraries.
/// </summary>
/// <remarks>
/// A connection value of the form <c>env:NAME</c> is read from the environment variable <c>NAME</c>, so a
/// password does not have to appear on the command line (where it lands in shell history and the process list).
/// </remarks>
internal static class StorageCopyCommandLine
{
    /// <summary>The help text.</summary>
    public const string Usage = """
        flow-orchestrator storage copy - copy a FlowOrchestrator database between SQL Server and PostgreSQL

        Usage:
          flow-orchestrator storage copy --from <provider> --from-connection <value>
                                         --to <provider> --to-connection <value> [options]

        Providers: sqlserver | postgresql

        Connection values: a connection string, or env:NAME to read it from the environment variable NAME.

        Options:
          --dry-run              Check and count only; write nothing (not even the target schema).
          --verify-only          Only compare source and target (row counts + content checksum).
          --skip-verify          Do not verify after copying (not recommended).
          --batch-size <n>       Rows per read / bulk-load batch (default 1000, max 100000).
          --resume               Continue an interrupted copy: skip tables already complete on the target.
          --force                Copy even when the source has runs in status Running.
          --migrate-source       Upgrade an older source schema with the 2.0 migrator instead of refusing.
          --skip-legacy-events   Do not copy the 1.x FlowEvents / flow_events table.
          --command-timeout <s>  Per-command timeout in seconds (default 600, 0 = none).
          --verbose              Print every batch and the storage migrators' log.
          --quiet                Print only errors and the summary.
          -h, --help             Show this help.
          --version              Show the tool version.

        Exit codes:
          0 success · 2 usage error · 3 preflight refused · 4 verification mismatch · 5 copy failed · 130 cancelled

        Example:
          flow-orchestrator storage copy --from sqlserver --from-connection env:FO_SQL \
                                         --to postgresql --to-connection env:FO_PG --dry-run
        """;

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="environment">Reads an environment variable (for <c>env:NAME</c> values).</param>
    /// <param name="version">The tool version, for <c>--version</c>.</param>
    public static ParseResult Parse(IReadOnlyList<string> args, Func<string, string?> environment, string version)
    {
        if (args.Count == 0 || IsHelp(args[0]))
            return new ParseResult { Output = Usage };
        if (args[0] == "--version")
            return new ParseResult { Output = version };
        if (args.Count < 2 || !string.Equals(args[0], "storage", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[1], "copy", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"Unknown command '{string.Join(' ', args.Take(2))}'. The only command is 'storage copy'.");
        }

        string? from = null, fromConnection = null, to = null, toConnection = null;
        bool dryRun = false, verifyOnly = false, skipVerify = false, resume = false, force = false;
        bool migrateSource = false, skipLegacy = false, verbose = false, quiet = false;
        var batchSize = FlowStorageCopyOptions.DefaultBatchSize;
        var timeout = 600;

        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            if (IsHelp(arg))
                return new ParseResult { Output = Usage };

            switch (arg)
            {
                case "--from": if (!TakeValue(ref i, out from)) return Missing(arg); break;
                case "--from-connection": if (!TakeValue(ref i, out fromConnection)) return Missing(arg); break;
                case "--to": if (!TakeValue(ref i, out to)) return Missing(arg); break;
                case "--to-connection": if (!TakeValue(ref i, out toConnection)) return Missing(arg); break;
                case "--batch-size":
                    if (!TakeValue(ref i, out var bs)) return Missing(arg);
                    if (!int.TryParse(bs, NumberStyles.None, CultureInfo.InvariantCulture, out batchSize)
                        || batchSize < 1 || batchSize > FlowStorageCopyOptions.MaxBatchSize)
                    {
                        return Fail($"--batch-size must be a whole number between 1 and {FlowStorageCopyOptions.MaxBatchSize}.");
                    }

                    break;
                case "--command-timeout":
                    if (!TakeValue(ref i, out var ct)) return Missing(arg);
                    if (!int.TryParse(ct, NumberStyles.None, CultureInfo.InvariantCulture, out timeout))
                        return Fail("--command-timeout must be a whole number of seconds (0 = none).");
                    break;
                case "--dry-run": dryRun = true; break;
                case "--verify-only": verifyOnly = true; break;
                case "--skip-verify": skipVerify = true; break;
                case "--resume": resume = true; break;
                case "--force": force = true; break;
                case "--migrate-source": migrateSource = true; break;
                case "--skip-legacy-events": skipLegacy = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                default:
                    return Fail($"Unknown option '{arg}'.");
            }

            if (inlineValue is not null && arg is "--dry-run" or "--verify-only" or "--skip-verify" or "--resume" or "--force"
                or "--migrate-source" or "--skip-legacy-events" or "--verbose" or "--quiet")
            {
                return Fail($"Option '{arg}' does not take a value.");
            }

            bool TakeValue(ref int index, out string? value)
            {
                if (inlineValue is not null)
                {
                    value = inlineValue;
                    return value.Length > 0;
                }

                if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++index];
                    return true;
                }

                value = null;
                return false;
            }
        }

        if (from is null) return Fail("--from is required (sqlserver or postgresql).");
        if (to is null) return Fail("--to is required (sqlserver or postgresql).");
        if (!FlowStorageProviderNames.TryParse(from, out var sourceProvider))
            return Fail($"--from '{from}' is not a provider; use sqlserver or postgresql.");
        if (!FlowStorageProviderNames.TryParse(to, out var targetProvider))
            return Fail($"--to '{to}' is not a provider; use sqlserver or postgresql.");
        if (fromConnection is null) return Fail("--from-connection is required.");
        if (toConnection is null) return Fail("--to-connection is required.");
        if (!TryResolve(fromConnection, environment, out var sourceCs, out var error)) return Fail("--from-connection: " + error);
        if (!TryResolve(toConnection, environment, out var targetCs, out error)) return Fail("--to-connection: " + error);
        if (verifyOnly && (dryRun || resume || force || migrateSource || skipVerify))
            return Fail("--verify-only cannot be combined with --dry-run, --resume, --force, --migrate-source or --skip-verify.");
        if (dryRun && (resume || skipVerify))
            return Fail("--dry-run cannot be combined with --resume or --skip-verify.");
        if (verbose && quiet)
            return Fail("--verbose and --quiet are mutually exclusive.");

        return new ParseResult
        {
            Command = new StorageCopyCommand
            {
                VerifyOnly = verifyOnly,
                Verbose = verbose,
                Quiet = quiet,
                Options = new FlowStorageCopyOptions
                {
                    SourceProvider = sourceProvider,
                    SourceConnectionString = sourceCs!,
                    TargetProvider = targetProvider,
                    TargetConnectionString = targetCs!,
                    BatchSize = batchSize,
                    DryRun = dryRun,
                    SkipVerify = skipVerify,
                    Resume = resume,
                    Force = force,
                    MigrateSourceSchema = migrateSource,
                    SkipLegacyEvents = skipLegacy,
                    CommandTimeoutSeconds = timeout,
                },
            },
        };
    }

    /// <summary>Resolves <c>env:NAME</c> to the variable's value; any other value is the connection string itself.</summary>
    internal static bool TryResolve(string value, Func<string, string?> environment, out string? connectionString, out string? error)
    {
        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = value[4..];
            connectionString = name.Length == 0 ? null : environment(name);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                error = $"environment variable '{name}' is not set or empty.";
                connectionString = null;
                return false;
            }

            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            connectionString = null;
            error = "the connection string is empty.";
            return false;
        }

        connectionString = value;
        error = null;
        return true;
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "-?" or "help";

    private static ParseResult Missing(string option) => Fail($"Option '{option}' needs a value.");

    private static ParseResult Fail(string message) => new() { Error = message + Environment.NewLine + "Run 'flow-orchestrator --help' for usage." };
}
