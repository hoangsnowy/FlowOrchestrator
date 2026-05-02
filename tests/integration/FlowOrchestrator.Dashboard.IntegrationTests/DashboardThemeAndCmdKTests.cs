using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the PR3 polish layer: dark theme tokens, density modifiers,
/// and the Cmd/Ctrl+K command palette. The bootstrap script must run BEFORE
/// CSS paints so dark-mode users do not see a flash-of-light-theme on every
/// page load — that requirement is encoded as a structural assertion here.
/// </summary>
public sealed class DashboardThemeAndCmdKTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    public DashboardThemeAndCmdKTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    // ── Dark theme tokens ─────────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_includes_dark_theme_token_block()
    {
        // Arrange — full token override so dark mode covers every surface.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("[data-theme=\"dark\"]", html);
        // Specific warm-dark surface tokens (no cool blue-grays).
        Assert.Contains("--fo-parchment:    #1c1b18", html);
        Assert.Contains("--fo-ivory:        #27272a", html);
    }

    [Fact]
    public async Task GET_root_bootstraps_theme_before_css_paint()
    {
        // Arrange — the inline <head> script must run before <style> so the
        // browser doesn't flash light theme on a dark-mode user's screen.

        // Act
        var html = await _body.Value;

        // Assert — script tag present, mentions localStorage + matchMedia.
        Assert.Contains("localStorage.getItem('fo-theme')", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        // The bootstrap script must precede the inline CSS in the document.
        var scriptIdx = html.IndexOf("localStorage.getItem('fo-theme')", StringComparison.Ordinal);
        var styleIdx = html.IndexOf("<style>", StringComparison.Ordinal);
        Assert.True(scriptIdx >= 0 && styleIdx >= 0, "Both bootstrap script and <style> block must exist");
        Assert.True(scriptIdx < styleIdx, "Theme bootstrap must run before paint to prevent FOUC");
    }

    [Fact]
    public async Task GET_root_has_theme_toggle_button()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"theme-toggle\"", html);
        Assert.Contains("function toggleTheme(", html);
        Assert.Contains("function applyTheme(", html);
    }

    // ── Density modifiers ─────────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_includes_density_modifier_tokens()
    {
        // Arrange — three density tiers map to padding/font-size token overrides.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("[data-density=\"compact\"]", html);
        Assert.Contains("[data-density=\"comfortable\"]", html);
        Assert.Contains("function setDensity(", html);
    }

    [Fact]
    public async Task GET_root_density_group_uses_aria_pressed()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("class=\"density-group\"", html);
        Assert.Contains("data-density=\"cozy\"", html);
        Assert.Contains("aria-pressed=\"true\"", html);
    }

    // ── Command palette (Cmd/Ctrl+K) ──────────────────────────────────────────

    [Fact]
    public async Task GET_root_includes_command_palette_dialog()
    {
        // Arrange — modal dialog with role + ARIA labelling.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"cmdk\"", html);
        Assert.Contains("role=\"dialog\"", html);
        Assert.Contains("aria-modal=\"true\"", html);
        Assert.Contains("id=\"cmdk-input\"", html);
        Assert.Contains("role=\"listbox\"", html);
    }

    [Fact]
    public async Task GET_root_inlines_command_palette_handlers()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function openCmdK(", html);
        Assert.Contains("function closeCmdK(", html);
        Assert.Contains("function renderCmdK(", html);
        Assert.Contains("function _cmdkScore(", html);
    }

    [Fact]
    public async Task GET_root_command_palette_listens_for_meta_or_ctrl_k()
    {
        // Arrange — key handler must check both Cmd (mac) and Ctrl (everyone else).

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("e.metaKey || e.ctrlKey", html);
    }

    // ── prefers-reduced-motion still honored after PR3 additions ─────────────

    [Fact]
    public async Task GET_root_still_honors_prefers_reduced_motion()
    {
        // Arrange — regress-guard the PR1 a11y win against later edits.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("@media (prefers-reduced-motion:reduce)", html);
    }
}
