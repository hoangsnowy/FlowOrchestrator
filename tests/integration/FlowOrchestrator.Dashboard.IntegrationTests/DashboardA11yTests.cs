using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down accessibility-critical markup in the rendered dashboard HTML.
/// Each test guards a single WCAG / ARIA invariant the dashboard depends on:
/// skip link, semantic nav, ARIA tablist, hamburger button, live regions,
/// and the absence of structural regressions from earlier <c>&lt;div onclick&gt;</c>
/// patterns. Tests run against the in-process <see cref="DashboardTestServer"/>;
/// no real database or runtime is required.
/// </summary>
public sealed class DashboardA11yTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    public DashboardA11yTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    // ── Skip link — keyboard users must reach main content in one tab ─────────

    [Fact]
    public async Task GET_root_includes_skip_link()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("class=\"skip-link\"", html);
        Assert.Contains("href=\"#main-content\"", html);
    }

    [Fact]
    public async Task GET_root_has_main_landmark_with_target_id()
    {
        // Arrange — the skip-link target must exist or focus jump fails silently.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"main-content\"", html);
        Assert.Contains("role=\"main\"", html);
    }

    // ── Semantic nav — buttons replace <div onclick=...> for keyboard a11y ────

    [Fact]
    public async Task GET_root_nav_items_are_buttons_not_divs()
    {
        // Arrange — divs with onclick aren't keyboard-focusable; we replaced
        // them with <button class="nav-item"> so Tab + Enter/Space just work.

        // Act
        var html = await _body.Value;

        // Assert — no remaining <div ...class="nav-item"...> patterns.
        Assert.DoesNotContain("<div class=\"nav-item", html);
        // And the buttons exist.
        Assert.Contains("<button type=\"button\" class=\"nav-item active\"", html);
        Assert.Contains("data-page=\"runs\"", html);
    }

    [Fact]
    public async Task GET_root_active_nav_item_advertises_aria_current()
    {
        // Arrange — screen readers announce the current page via aria-current.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("aria-current=\"page\"", html);
    }

    // ── Hamburger button — mobile nav toggle, ARIA-aware ──────────────────────

    [Fact]
    public async Task GET_root_includes_hamburger_buttons_with_aria()
    {
        // Arrange — at least one hamburger per page header (4 pages).

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("class=\"hamburger\"", html);
        Assert.Contains("aria-label=\"Toggle navigation menu\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("aria-controls=\"sidebar\"", html);
    }

    // ── ARIA tablist on flow detail tabs ──────────────────────────────────────

    [Fact]
    public async Task GET_root_flow_detail_tabs_use_tablist_pattern()
    {
        // Arrange — buttons must be role="tab", container role="tablist",
        // and the active tab must declare aria-selected="true".

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("role=\"tablist\"", html);
        Assert.Contains("role=\"tab\"", html);
        Assert.Contains("aria-selected=\"true\"", html);
        Assert.Contains("role=\"tabpanel\"", html);
        // Roving tabindex pattern — only one tab is in the focus order.
        Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("tabindex=\"-1\"", html);
    }

    [Fact]
    public async Task GET_root_tabpanels_reference_owning_tab()
    {
        // Arrange — aria-labelledby links the panel to its tab so screen
        // readers announce both at once.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("aria-labelledby=\"tab-fd-manifest\"", html);
        Assert.Contains("id=\"tab-fd-manifest\"", html);
    }

    // ── Live region for toast notifications ───────────────────────────────────

    [Fact]
    public async Task GET_root_includes_toast_live_region()
    {
        // Arrange — transient feedback (Copy!, Triggered!) must live in a
        // single role="status" container so screen readers receive updates.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"toast-region\"", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("aria-atomic=\"true\"", html);
    }

    // ── Form-control labelling — search/filter inputs need labels ─────────────

    [Fact]
    public async Task GET_root_search_input_has_aria_label()
    {
        // Arrange — visible placeholder is not a substitute for a label.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("aria-label=\"Search runs\"", html);
        Assert.Contains("aria-label=\"Filter by flow\"", html);
        Assert.Contains("aria-label=\"Filter by status\"", html);
    }

    [Fact]
    public async Task GET_root_includes_visually_hidden_label_helper()
    {
        // Arrange — the .visually-hidden utility powers accessible labels
        // that don't show in the layout. Guard the CSS rule presence.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains(".visually-hidden", html);
    }

    // ── Responsive design — required for full-mobile coverage ─────────────────

    [Fact]
    public async Task GET_root_includes_mobile_breakpoint()
    {
        // Arrange — without a 767px-or-narrower media query the dashboard
        // overflows below tablet width.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("@media (max-width:767px)", html);
        Assert.Contains("@media (max-width:991px)", html);
    }

    [Fact]
    public async Task GET_root_honors_prefers_reduced_motion()
    {
        // Arrange — users with vestibular sensitivity must be able to
        // suppress the pulsing dot and skeleton shimmer.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("@media (prefers-reduced-motion:reduce)", html);
    }

    // ── Focus-visible rings present (keyboard navigation hints) ───────────────

    [Fact]
    public async Task GET_root_includes_focus_visible_outline()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains(":focus-visible", html);
        Assert.Contains("outline:2px solid var(--fo-focus)", html);
    }

    // ── New JS hooks for mobile drawer + tablist keyboard nav ─────────────────

    [Fact]
    public async Task GET_root_inlines_toggle_sidebar_function()
    {
        // Arrange — the hamburger handler calls toggleSidebar(); regress-guard
        // the function lives in the served JS bundle.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function toggleSidebar(", html);
        Assert.Contains("function closeSidebar(", html);
    }

    // ── Color-drift regression: dashboard must not ship raw purple anymore ────

    [Fact]
    public async Task GET_root_does_not_ship_raw_purple_drift_colors()
    {
        // Arrange — these out-of-palette colors used to live in the Scheduled
        // stat card and the .btn-sm-trigger button. They were replaced with
        // semantic tokens in PR1; this test guards against re-introduction.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.DoesNotContain("#8b5cf6", html);
        Assert.DoesNotContain("#7C3AED", html);
        Assert.DoesNotContain("#C4B5FD", html);
    }
}
