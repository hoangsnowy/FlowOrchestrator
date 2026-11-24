using System.Data.Common;
using System.Globalization;
using System.Reflection;
using FlowOrchestrator.StorageMigration;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Cli;

/// <summary>Process exit codes of <c>flow-orchestrator</c>.</summary>
internal static class ExitCodes
{
    /// <summary>The copy (or verification) succeeded.</summary>
    public const int Success = 0;

    /// <summary>An unexpected error.</summary>
    public const int Unexpected = 1;

    /// <summary>The command line is invalid.</summary>
    public const int Usage = 2;

    /// <summary>A preflight rule refused the copy; nothing was written.</summary>
    public const int PreflightRefused = 3;

    /// <summary>Source and target differ after the copy, or in <c>--verify-only</c>.</summary>
    public const int VerificationFailed = 4;

    /// <summary>The copy failed part-way (a row the target cannot store, a database error).</summary>
    public const int CopyFailed = 5;

    /// <summary>Cancelled with Ctrl+C.</summary>
    public const int Cancelled = 130;
}

/// <summary>Runs one <c>flow-orchestrator</c> invocation; the console is injected so it is testable.</summary>
internal static class CliApp
{
    /// <summary>Executes a copy or a verification; replaceable in tests.</summary>
    public delegate Task<FlowStorageCopyResult> Executor(
        FlowStorageCopyOptions options, bool verifyOnly, IProgress<FlowStorageCopyProgress> progress, CancellationToken cancellationToken);

    /// <summary>Runs the copier for real.</summary>
    public static Task<FlowStorageCopyResult> ExecuteAsync(
        FlowStorageCopyOptions options, bool verifyOnly, IProgress<FlowStorageCopyProgress> progress, CancellationToken cancellationToken)
    {
        var copier = new FlowStorageCopier(options);
        return verifyOnly ? copier.VerifyAsync(progress, cancellationToken) : copier.CopyAsync(progress, cancellationToken);
    }

    /// <summary>Parses <paramref name="args"/>, runs the command and returns the process exit code.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        CancellationToken cancellationToken,
        Executor? executor = null)
    {
        var parsed = StorageCopyCommandLine.Parse(args, environment, Version);
        if (parsed.Output is not null)
        {
            await stdout.WriteLineAsync(parsed.Output).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (parsed.Error is not null)
        {
            await stderr.WriteLineAsync(parsed.Error).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        var command = parsed.Command!;
        using var loggerFactory = command.Verbose ? LoggerFactory.Create(b => b.AddProvider(new TextWriterLoggerProvider(stderr))) : null;
        command.Options.LoggerFactory = loggerFactory;
        var progress = new ConsoleProgress(stdout, command.Verbose, command.Quiet);

        var mode = command.VerifyOnly ? "Verifying" : command.Options.DryRun ? "Dry run:" : "Copying";
        if (!command.Quiet)
        {
            await stdout.WriteLineAsync(
                $"{mode} {FlowStorageProviderNames.ToName(command.Options.SourceProvider)} -> {FlowStorageProviderNames.ToName(command.Options.TargetProvider)}").ConfigureAwait(false);
        }

        try
        {
            var result = await (executor ?? ExecuteAsync)(command.Options, command.VerifyOnly, progress, cancellationToken).ConfigureAwait(false);
            await WriteSummaryAsync(stdout, result).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Cancelled. The table being copied was rolled back; tables already copied stay on the target (use --resume to continue).").ConfigureAwait(false);
            return ExitCodes.Cancelled;
        }
        catch (FlowStoragePreflightException ex)
        {
            await stderr.WriteLineAsync($"Refused ({ex.Failure}): {ex.Message}").ConfigureAwait(false);
            return ExitCodes.PreflightRefused;
        }
        catch (FlowStorageVerificationException ex)
        {
            await WriteSummaryAsync(stdout, ex.Result).ConfigureAwait(false);
            await stderr.WriteLineAsync("Verification failed: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.VerificationFailed;
        }
        catch (FlowStorageCopyException ex)
        {
            await stderr.WriteLineAsync("Copy failed: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.CopyFailed;
        }
        catch (DbException ex)
        {
            await stderr.WriteLineAsync($"Database error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.CopyFailed;
        }
        catch (ArgumentException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
    }

    /// <summary>The tool's informational version.</summary>
    internal static string Version =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliApp).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static async Task WriteSummaryAsync(TextWriter stdout, FlowStorageCopyResult result)
    {
        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{"Table",-22} {"Source rows",12} {"Copied",12} {"Target rows",12}  Verified")).ConfigureAwait(false);
        foreach (var t in result.Tables)
        {
            var verified = t.Verified switch { true => "yes", false => "NO", null => "-" };
            var copied = t.SkippedAsComplete ? "skipped" : t.CopiedRows.ToString(CultureInfo.InvariantCulture);
            var targetRows = t.TargetRows?.ToString(CultureInfo.InvariantCulture) ?? "-";
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{t.Table,-22} {t.SourceRows,12} {copied,12} {targetRows,12}  {verified}")).ConfigureAwait(false);
        }

        foreach (var note in result.Notes)
            await stdout.WriteLineAsync("note: " + note).ConfigureAwait(false);

        var outcome = result.DryRun ? "Dry run finished" : result.VerifyOnly ? "Verification finished" : "Copy finished";
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{outcome} in {result.Elapsed.TotalSeconds:0.0}s: {result.TotalCopiedRows} rows copied{(result.IsVerified ? ", all tables verified" : string.Empty)}.")).ConfigureAwait(false);
    }

    /// <summary>Prints progress: one line per table and phase, every batch with <c>--verbose</c>.</summary>
    private sealed class ConsoleProgress : IProgress<FlowStorageCopyProgress>
    {
        private readonly TextWriter _out;
        private readonly bool _verbose;
        private readonly bool _quiet;
        private readonly Lock _gate = new();

        public ConsoleProgress(TextWriter output, bool verbose, bool quiet)
        {
            _out = output;
            _verbose = verbose;
            _quiet = quiet;
        }

        public void Report(FlowStorageCopyProgress value)
        {
            if (_quiet)
                return;
            // Batch reports are noise for a large table unless asked for; the first and the last are kept.
            var isBatch = value.Phase == FlowStorageCopyPhase.Copy && value.RowsDone > 0 && value.RowsDone != value.RowsTotal;
            if (isBatch && !_verbose)
                return;
            lock (_gate)
                _out.WriteLine("  " + value.Message);
        }
    }

    /// <summary>A minimal logger that writes warnings and above (everything with <c>--verbose</c>) to a writer.</summary>
    private sealed class TextWriterLoggerProvider : ILoggerProvider
    {
        private readonly TextWriter _writer;

        public TextWriterLoggerProvider(TextWriter writer) => _writer = writer;

        public ILogger CreateLogger(string categoryName) => new Logger(_writer, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly TextWriter _writer;
            private readonly string _category;

            public Logger(TextWriter writer, string category)
            {
                _writer = writer;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                var category = _category[(_category.LastIndexOf('.') + 1)..];
                _writer.WriteLine($"  [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " - " + exception.Message)}");
            }
        }
    }
}
