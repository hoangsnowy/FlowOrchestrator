using FlowOrchestrator.Cli;

namespace FlowOrchestrator.StorageMigration.UnitTests;

/// <summary>
/// The <c>flow-orchestrator storage copy</c> command line: option parsing, <c>env:NAME</c> connection values,
/// conflicting flags, and the exit code for every outcome of a run.
/// </summary>
public sealed class CliTests
{
    private static readonly Dictionary<string, string> Env = new()
    {
        ["FO_SQL"] = "Server=sql;Database=flows;User Id=sa;Password=secret",
        ["FO_PG"] = "Host=pg;Database=flows;Username=fo;Password=secret",
    };

    private static ParseResult Parse(params string[] args) =>
        StorageCopyCommandLine.Parse(args, name => Env.GetValueOrDefault(name), "9.9.9");

    private static readonly string[] Minimal =
        ["storage", "copy", "--from", "sqlserver", "--from-connection", "env:FO_SQL", "--to", "postgresql", "--to-connection", "env:FO_PG"];

    [Fact]
    public void A_full_command_line_maps_to_the_copier_options()
    {
        // Arrange
        string[] args = [.. Minimal, "--batch-size", "250", "--force", "--migrate-source", "--skip-legacy-events", "--command-timeout=0", "--verbose"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Null(result.Error);
        var command = Assert.IsType<StorageCopyCommand>(result.Command);
        Assert.Equal(FlowStorageProvider.SqlServer, command.Options.SourceProvider);
        Assert.Equal(Env["FO_SQL"], command.Options.SourceConnectionString);
        Assert.Equal(FlowStorageProvider.PostgreSql, command.Options.TargetProvider);
        Assert.Equal(Env["FO_PG"], command.Options.TargetConnectionString);
        Assert.Equal(250, command.Options.BatchSize);
        Assert.True(command.Options.Force);
        Assert.True(command.Options.MigrateSourceSchema);
        Assert.True(command.Options.SkipLegacyEvents);
        Assert.Equal(0, command.Options.CommandTimeoutSeconds);
        Assert.True(command.Verbose);
        Assert.False(command.VerifyOnly);
        Assert.False(command.Options.DryRun);
    }

    [Fact]
    public void A_literal_connection_string_with_equals_signs_is_kept_whole_in_both_spellings()
    {
        // Arrange
        const string cs = "Server=.;Database=flows;Trusted_Connection=True;";

        // Act
        var separate = Parse("storage", "copy", "--from", "sqlserver", "--from-connection", cs, "--to", "pg", "--to-connection", "env:FO_PG");
        var inline = Parse("storage", "copy", "--from", "sqlserver", "--from-connection=" + cs, "--to", "pg", "--to-connection", "env:FO_PG");

        // Assert
        Assert.Equal(cs, separate.Command!.Options.SourceConnectionString);
        Assert.Equal(cs, inline.Command!.Options.SourceConnectionString);
    }

    [Fact]
    public void An_unset_environment_variable_is_a_usage_error_naming_it()
    {
        // Arrange
        string[] args = ["storage", "copy", "--from", "sqlserver", "--from-connection", "env:NOPE", "--to", "postgresql", "--to-connection", "env:FO_PG"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Null(result.Command);
        Assert.Contains("environment variable 'NOPE' is not set", result.Error);
    }

    [Theory]
    [InlineData("--from", "mysql", "is not a provider")]
    [InlineData("--batch-size", "0", "--batch-size must be")]
    [InlineData("--batch-size", "abc", "--batch-size must be")]
    [InlineData("--bogus", "x", "Unknown option '--bogus'")]
    public void Invalid_values_are_usage_errors(string option, string value, string expected)
    {
        // Arrange
        var args = new List<string>(Minimal);
        var at = args.IndexOf(option);
        if (at >= 0)
            args[at + 1] = value;
        else
            args.AddRange([option, value]);

        // Act
        var result = Parse([.. args]);

        // Assert
        Assert.Contains(expected, result.Error);
    }

    [Theory]
    [InlineData("--verify-only", "--dry-run")]
    [InlineData("--verify-only", "--resume")]
    [InlineData("--dry-run", "--skip-verify")]
    [InlineData("--verbose", "--quiet")]
    public void Conflicting_flags_are_usage_errors(string first, string second)
    {
        // Arrange
        string[] args = [.. Minimal, first, second];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void A_missing_required_option_is_named()
    {
        // Arrange
        string[] args = ["storage", "copy", "--from", "sqlserver", "--from-connection", "env:FO_SQL", "--to-connection", "env:FO_PG"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Contains("--to is required", result.Error);
    }

    [Fact]
    public void A_flag_given_a_value_is_a_usage_error()
    {
        // Arrange
        string[] args = [.. Minimal, "--force=yes"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Contains("does not take a value", result.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_prints_the_usage(string flag)
    {
        // Arrange
        string[] args = [flag];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Contains("flow-orchestrator storage copy --from <provider>", result.Output);
    }

    [Fact]
    public void Version_prints_the_tool_version()
    {
        // Arrange
        string[] args = ["--version"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Equal("9.9.9", result.Output);
    }

    [Fact]
    public void An_unknown_verb_is_a_usage_error()
    {
        // Arrange
        string[] args = ["storage", "move"];

        // Act
        var result = Parse(args);

        // Assert
        Assert.Contains("The only command is 'storage copy'", result.Error);
    }

    public static TheoryData<string, int> Outcomes() => new()
    {
        { "ok", 0 },
        { "preflight", 3 },
        { "verify", 4 },
        { "copy", 5 },
        { "cancel", 130 },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Every_outcome_maps_to_its_exit_code(string outcome, int expected)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var result = new FlowStorageCopyResult
        {
            Tables = [new FlowStorageTableResult { Table = "FlowRuns", SourceName = "FlowRuns", TargetName = "flow_runs", SourceRows = 3, CopiedRows = 3, TargetRows = 3, Verified = outcome != "verify" }],
            Notes = ["a note"],
        };
        CliApp.Executor executor = (_, _, _, ct) => outcome switch
        {
            "ok" => Task.FromResult(result),
            "preflight" => throw new FlowStoragePreflightException(FlowStoragePreflightFailure.ActiveRuns, "2 runs are Running."),
            "verify" => throw new FlowStorageVerificationException("FlowRuns differs.", result),
            "copy" => throw new FlowStorageCopyException("FlowSteps", "row too long"),
            _ => Cancel(cts),
        };

        // Act
        var code = await CliApp.RunAsync(Minimal, stdout, stderr, name => Env.GetValueOrDefault(name), cts.Token, executor);

        // Assert
        Assert.Equal(expected, code);
        if (outcome == "ok")
        {
            Assert.Contains("FlowRuns", stdout.ToString());
            Assert.Contains("note: a note", stdout.ToString());
            Assert.Contains("all tables verified", stdout.ToString());
        }

        if (outcome == "preflight")
            Assert.Contains("Refused (ActiveRuns): 2 runs are Running.", stderr.ToString());
        if (outcome == "verify")
            Assert.Contains("NO", stdout.ToString());
    }

    private static Task<FlowStorageCopyResult> Cancel(CancellationTokenSource cts)
    {
        cts.Cancel();
        throw new OperationCanceledException(cts.Token);
    }

    [Fact]
    public async Task A_usage_error_exits_2_without_running_anything()
    {
        // Arrange
        var ran = false;
        CliApp.Executor executor = (_, _, _, _) =>
        {
            ran = true;
            return Task.FromResult(new FlowStorageCopyResult { Tables = [], Notes = [] });
        };
        var stderr = new StringWriter();

        // Act
        var code = await CliApp.RunAsync(["storage", "copy"], new StringWriter(), stderr, _ => null, CancellationToken.None, executor);

        // Assert
        Assert.Equal(2, code);
        Assert.False(ran);
        Assert.Contains("--from is required", stderr.ToString());
    }

    [Fact]
    public async Task Verify_only_is_passed_to_the_executor()
    {
        // Arrange
        bool? verifyOnly = null;
        CliApp.Executor executor = (_, v, _, _) =>
        {
            verifyOnly = v;
            return Task.FromResult(new FlowStorageCopyResult { Tables = [], Notes = [], VerifyOnly = v });
        };

        // Act
        var code = await CliApp.RunAsync([.. Minimal, "--verify-only"], new StringWriter(), new StringWriter(), name => Env.GetValueOrDefault(name), CancellationToken.None, executor);

        // Assert
        Assert.Equal(0, code);
        Assert.True(verifyOnly);
    }
}
