using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the output-encoding contract of the served dashboard script: the shared
/// <c>esc()</c> helper encodes quotes so its result is safe inside attribute values, inline
/// handler arguments are JS-escaped and then HTML-encoded, and the run-status filter read from
/// the URL hash is allow-listed. Content-level checks — the repository has no JS test harness.
/// </summary>
public sealed class DashboardEscapingContractTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    /// <summary>Starts the dashboard and lazily fetches the served page once per test.</summary>
    public DashboardEscapingContractTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    /// <inheritdoc/>
    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Esc_delegates_to_a_quote_safe_encoder()
    {
        // Arrange — textContent→innerHTML (the old esc) leaves " and ' unencoded.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function esc(s) { return s ? escapeHtml(s) : ''; }", html, StringComparison.Ordinal);
        Assert.Contains("String(s).replace(/[&<>\"']/g", html, StringComparison.Ordinal);
        Assert.Contains("c === '\"' ? '&quot;' : '&#39;'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("d.textContent=s; return d.innerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapeHtml_is_defined_exactly_once()
    {
        // Arrange — a second definition would silently win (function hoisting) and could drift.

        // Act
        var html = await _body.Value;

        // Assert
        var first = html.IndexOf("function escapeHtml(", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, html.IndexOf("function escapeHtml(", first + 1, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inline_handler_arguments_are_js_escaped_then_html_encoded()
    {
        // Arrange — an HTML-encoded value is decoded back before an inline handler runs, so a
        // quote in a step key or job id must be JS-escaped as well.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function escJsAttr(s) { return escapeHtml(escJsString(s)); }", html, StringComparison.Ordinal);
        Assert.DoesNotContain("const safeKey = escJsString(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("+esc(s.stepKey)+'\\'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("+esc(j.jobId)+'\\'", html, StringComparison.Ordinal);
        Assert.Contains("retryStep(\\''+escJsAttr(runId)+'\\',\\''+escJsAttr(s.stepKey)+'\\'", html, StringComparison.Ordinal);
        Assert.Contains("triggerScheduledJob(\\''+escJsAttr(j.jobId)+'\\'", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Svg_tooltips_encode_step_type_and_blocking_step()
    {
        // Arrange — the DAG node <title> is built by string concatenation from manifest data.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("const tooltip = esc(key)+'\\n'+esc(step.type||'')", html, StringComparison.Ordinal);
        Assert.Contains("'\\nBlocked because \"'+esc(blockedOn)+'\" did not succeed.'", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_allow_lists_the_status_filter()
    {
        // Arrange — the filter chip renders the hash's status value into the page.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains(
            "const RUN_STATUS_FILTERS = ['Running', 'Succeeded', 'Skipped', 'Failed', 'Cancelled', 'TimedOut'];",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (params.status !== undefined && !RUN_STATUS_FILTERS.includes(params.status)) delete params.status;",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_allow_list_matches_the_filter_dropdown_options()
    {
        // Arrange — the allow-list must stay in step with the <select> it guards.
        string[] statuses = ["Running", "Succeeded", "Skipped", "Failed", "Cancelled", "TimedOut"];

        // Act
        var html = await _body.Value;

        // Assert
        foreach (var status in statuses)
        {
            Assert.Contains($"<option value=\"{status}\">", html, StringComparison.Ordinal);
        }
    }
}
