using System.Text.RegularExpressions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the operator-focused dashboard redesign: colour-token discipline outside the
/// <c>:root</c> palette, the single-sans typography rule, the "needs attention" Overview, the
/// failure summary on run detail, and the escaping of the new inline handlers.
/// </summary>
public sealed class DashboardOperatorUiContractTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    /// <summary>Creates the test server and lazily fetches the served dashboard page once.</summary>
    public DashboardOperatorUiContractTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    /// <summary>Disposes the in-process test server.</summary>
    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Css_rules_after_the_token_blocks_use_no_colour_literals()
    {
        // Arrange — everything after the last density modifier is rule CSS, which must only
        // reference --fo-* tokens (CLAUDE.md "Dashboard UI Standards").
        var html = await _body.Value;
        var css = InlineCss(html);
        var rulesStart = css.IndexOf("[data-density=\"comfortable\"]{", StringComparison.Ordinal);

        // Act
        var rules = css[rulesStart..];
        var hexLiterals = HexColourInValue().Matches(rules).Select(m => m.Value).ToArray();
        var rgbLiterals = RgbFunction().Matches(rules).Select(m => m.Value).ToArray();

        // Assert
        Assert.True(rulesStart > 0, "density modifier block not found");
        Assert.Empty(hexLiterals);
        Assert.Empty(rgbLiterals);
        Assert.DoesNotContain("linear-gradient", rules, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_loads_one_sans_and_one_mono_family_only()
    {
        // Arrange

        // Act
        var html = await _body.Value;
        var css = InlineCss(html);

        // Assert
        Assert.Contains("family=Inter", css, StringComparison.Ordinal);
        Assert.Contains("family=JetBrains+Mono", css, StringComparison.Ordinal);
        Assert.DoesNotContain("Source+Serif", css, StringComparison.Ordinal);
        Assert.DoesNotContain("font-family:Georgia", css, StringComparison.Ordinal);
        Assert.DoesNotContain("var(--fo-serif)", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inline_script_draws_svg_and_status_colours_from_tokens()
    {
        // Arrange — the DAG and Gantt used to hard-code light-theme hex fills.

        // Act
        var html = await _body.Value;
        var script = InlineScript(html);

        // Assert
        Assert.DoesNotMatch(HexColourString(), script);
        Assert.DoesNotContain("rgba(", script, StringComparison.Ordinal);
        Assert.Contains("class=\"dag-node-bg\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"gantt-bar s-'+st", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_surfaces_failed_and_running_runs_first()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"ov-attention\"", html, StringComparison.Ordinal);
        Assert.Contains("BASE+'/runs?status=Failed&take=8'", html, StringComparison.Ordinal);
        Assert.Contains("BASE+'/runs/active'", html, StringComparison.Ordinal);
        Assert.Contains("onclick=\"openRunsWithStatus('Failed')\"", html, StringComparison.Ordinal);
        // The decorative KPI-card grid and 30-day calendar are gone.
        Assert.DoesNotContain("class=\"stat-card", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"ov-calendar\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_detail_shows_failures_above_tabs_and_keeps_panel_state()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("'<div class=\"run-failures\" role=\"alert\">'", html, StringComparison.Ordinal);
        Assert.Contains("esc(firstLine(s.errorMessage))", html, StringComparison.Ordinal);
        Assert.Contains("const _panelOpenState = new Map();", html, StringComparison.Ordinal);
        Assert.Contains("_panelOpenState.has(pk) ? _panelOpenState.get(pk) : !!openByDefault", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_inline_handlers_escape_server_ids()
    {
        // Arrange — every id that reaches an onclick="fn('…')" goes through escJsAttr
        // (GHSA-358q-p9gq-3m6p).

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("openRun(\\''+escJsAttr(id)+'\\')", html, StringComparison.Ordinal);
        Assert.Contains("openFlowDetail(\\''+fid+'\\')", html, StringComparison.Ordinal);
        Assert.Contains("const fid = escJsAttr(f.id);", html, StringComparison.Ordinal);
        Assert.Contains("selectRun(\\''+rid+'\\')", html, StringComparison.Ordinal);
        Assert.Contains("const rid = escJsAttr(r.id);", html, StringComparison.Ordinal);
        Assert.Contains("selectStep(\\''+escJsAttr(s.stepKey)+'\\')", html, StringComparison.Ordinal);
        Assert.DoesNotContain("selectRun(\\''+r.id+'\\')", html, StringComparison.Ordinal);
        Assert.DoesNotContain("openFlowDetail(\\''+f.id+'\\')", html, StringComparison.Ordinal);
    }

    private static string InlineCss(string html)
    {
        var start = html.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length;
        var end = html.IndexOf("</style>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static string InlineScript(string html)
    {
        var start = html.LastIndexOf("<script>", StringComparison.Ordinal) + "<script>".Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    // Plain Regex instances, not [GeneratedRegex]: the source generator writes code into obj/, which
    // CodeQL then analyses and flags (cs/useless-assignment-to-local, cs/useless-cast-to-self) as ours.
    private static readonly Regex HexColourInValueRegex = new(@"(?<=[:\s,(])#[0-9a-fA-F]{3,8}\b", RegexOptions.CultureInvariant);
    private static readonly Regex RgbFunctionRegex = new(@"\brgba?\(", RegexOptions.CultureInvariant);
    private static readonly Regex HexColourStringRegex = new(@"[""']#[0-9a-fA-F]{6}[""']", RegexOptions.CultureInvariant);

    private static Regex HexColourInValue() => HexColourInValueRegex;

    private static Regex RgbFunction() => RgbFunctionRegex;

    private static Regex HexColourString() => HexColourStringRegex;
}
