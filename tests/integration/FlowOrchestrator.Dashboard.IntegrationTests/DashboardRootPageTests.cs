using System.Net;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Verifies that <c>GET /flows</c> serves a fully-assembled dashboard HTML
/// document. Guards both the design-system token migration (Phase A) and the
/// embedded-resource extraction (Phase C) of <see cref="DashboardHtml"/>:
/// the page must include the new <c>--fo-*</c> palette, must have the CSS
/// and JS resource bodies inlined, must contain the structural HTML markers,
/// and must not leak any <c>{{INLINE_*}}</c> placeholders.
/// </summary>
public sealed class DashboardRootPageTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    public DashboardRootPageTests()
    {
        _client = _server.CreateClient();
        // Some endpoints rely on FlowStore; the root page does not, but the
        // shared default keeps integration assertions stable.
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    // ── Phase A: design-system token reachability ─────────────────────────────

    [Fact]
    public async Task GET_root_includes_fo_terracotta_token()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("--fo-terracotta:", html);
        Assert.Contains("#c96442", html);
    }

    [Fact]
    public async Task GET_root_includes_fo_parchment_token()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("--fo-parchment:", html);
        Assert.Contains("#f5f4ed", html);
    }

    // ── Phase C: HTML structural markers (resource assembly didn't drop tags) ─

    [Theory]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("<style>")]
    [InlineData("</style>")]
    [InlineData("<script>")]
    [InlineData("</script>")]
    [InlineData("</body>")]
    [InlineData("</html>")]
    public async Task GET_root_contains_structural_html_marker(string marker)
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains(marker, html);
    }

    // ── Phase C: CSS body inlined into shell ──────────────────────────────────

    [Fact]
    public async Task GET_root_inlines_dashboard_css_body()
    {
        // Arrange — '.sidebar-brand' is a stable rule defined in dashboard.css.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains(".sidebar-brand", html);
    }

    // ── Phase C: JS body inlined into shell ───────────────────────────────────

    [Fact]
    public async Task GET_root_inlines_dashboard_js_body()
    {
        // Arrange — 'function fetchJSON(' is a stable function defined in dashboard.js.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function fetchJSON(", html);
    }

    // ── Phase C: placeholders fully substituted (no leakage) ──────────────────

    [Fact]
    public async Task GET_root_does_not_leak_inline_css_placeholder()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.DoesNotContain("{{INLINE_CSS}}", html);
    }

    [Fact]
    public async Task GET_root_does_not_leak_inline_js_placeholder()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.DoesNotContain("{{INLINE_JS}}", html);
    }

    [Fact]
    public async Task GET_root_does_not_leak_branding_placeholders()
    {
        // Arrange — base path / title / brand tokens must all be substituted at render time.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.DoesNotContain("{{BASE_PATH}}", html);
        Assert.DoesNotContain("{{PAGE_TITLE}}", html);
        Assert.DoesNotContain("{{BRAND_TITLE}}", html);
        Assert.DoesNotContain("{{BRAND_SUBTITLE}}", html);
        Assert.DoesNotContain("{{BRAND_LOGO}}", html);
    }

    // ── Smoke: response shape ─────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_returns_200_and_html_content_type()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
    }
}
