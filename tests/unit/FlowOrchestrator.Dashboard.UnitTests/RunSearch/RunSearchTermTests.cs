namespace FlowOrchestrator.Dashboard.UnitTests.RunSearch;

/// <summary>
/// Pins how <see cref="RunSearchTerm.Parse"/> classifies a run-search term. Each shortcut here is what
/// keeps a search off the <c>%term%</c> scan path: a run id becomes a primary-key seek and a status
/// name becomes the status filter.
/// </summary>
public sealed class RunSearchTermTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    public void Parse_blank_term_is_none(string? raw)
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.None, term.Kind);
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("  3f2504e0-4f89-11d3-9a0c-0305e82c3301  ")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData("3f2504e04f8911d39a0c0305e82c3301")]
    public void Parse_full_guid_is_a_run_id(string raw)
    {
        // Arrange
        var expected = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.RunId, term.Kind);
        Assert.Equal(expected, term.RunId);
        Assert.Null(term.Text);
    }

    [Fact]
    public void Parse_guid_with_step_payload_search_stays_text()
    {
        // Arrange — a payload search exists to find ids (order ids…) inside step output.
        const string raw = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: true);

        // Assert
        Assert.Equal(RunSearchTermKind.Text, term.Kind);
        Assert.Equal(raw, term.Text);
        Assert.Null(term.RunId);
    }

    [Fact]
    public void Parse_guid_fragment_is_text()
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse("3f2504e0", hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Text, term.Kind);
        Assert.Equal("3f2504e0", term.Text);
    }

    [Theory]
    [InlineData("Succeeded", "Succeeded")]
    [InlineData("succeeded", "Succeeded")]
    [InlineData("FAILED", "Failed")]
    [InlineData("  Running  ", "Running")]
    [InlineData("skipped", "Skipped")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("timedout", "TimedOut")]
    [InlineData("Blocked", "Skipped")]
    [InlineData("timed out", "TimedOut")]
    public void Parse_status_name_or_display_label_is_a_status_without_alias(string raw, string expected)
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Status, term.Kind);
        Assert.Equal(expected, term.Status);
        Assert.Null(term.Alias);
        Assert.Null(term.Text);
    }

    [Theory]
    [InlineData("Completed", "Succeeded")]
    [InlineData("completed", "Succeeded")]
    [InlineData("Success", "Succeeded")]
    [InlineData("Done", "Succeeded")]
    [InlineData("Failure", "Failed")]
    [InlineData("Canceled", "Cancelled")]
    [InlineData("Timeout", "TimedOut")]
    [InlineData("In progress", "Running")]
    public void Parse_status_synonym_maps_and_reports_alias(string raw, string expected)
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Status, term.Kind);
        Assert.Equal(expected, term.Status);
        Assert.Equal(raw, term.Alias);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Completed")]
    [InlineData("blocked")]
    public void Parse_explicit_status_filter_wins_and_term_stays_text(string raw)
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: true, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Text, term.Kind);
        Assert.Equal(raw, term.Text);
        Assert.Null(term.Status);
    }

    [Fact]
    public void Parse_status_name_applies_with_step_payload_search_too()
    {
        // Arrange — the shortcut runs before any LIKE, whichever columns the LIKE would cover.

        // Act
        var term = RunSearchTerm.Parse("Failed", hasExplicitStatus: false, includeStepPayloads: true);

        // Assert
        Assert.Equal(RunSearchTermKind.Status, term.Kind);
        Assert.Equal("Failed", term.Status);
    }

    [Theory]
    [InlineData("\"Completed\"", "Completed")]
    [InlineData("\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("\" Failed \"", "Failed")]
    public void Parse_quoted_term_skips_every_shortcut(string raw, string expected)
    {
        // Arrange

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Text, term.Kind);
        Assert.Equal(expected, term.Text);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("Completed orders")]
    [InlineData("OrderFlow")]
    public void Parse_free_text_is_text(string raw)
    {
        // Arrange — "error" is deliberately not a Failed synonym: it is as likely to be a flow name
        // fragment as a status.

        // Act
        var term = RunSearchTerm.Parse(raw, hasExplicitStatus: false, includeStepPayloads: false);

        // Assert
        Assert.Equal(RunSearchTermKind.Text, term.Kind);
        Assert.Equal(raw, term.Text);
    }
}
