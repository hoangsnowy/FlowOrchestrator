using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the runtime JS contract introduced in the PR2 polish pass:
/// the Router IIFE, AbortController plumbing, central toast/error feedback,
/// withBusy helper, smart auto-refresh pause, skeleton helpers, and the
/// removal of every <c>alert()</c> call. These are content-level checks
/// against the served HTML — they catch accidental regressions when later
/// edits remove or rename a load-bearing symbol.
/// </summary>
public sealed class DashboardJsContractTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    public DashboardJsContractTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    // ── Hardline regression: no more alert() in the served bundle ─────────────

    [Fact]
    public async Task GET_root_does_not_contain_any_alert_calls()
    {
        // Arrange — the toast/error system replaced every alert(...) site.

        // Act
        var html = await _body.Value;

        // Assert — guard against re-introduction.
        Assert.DoesNotContain("alert(", html, StringComparison.Ordinal);
    }

    // ── Router module surface ─────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_inlines_router_module()
    {
        // Arrange — the IIFE owns parse + go + replaceParams + on.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("const Router = (function", html);
        Assert.Contains("Router.parse(", html);
        Assert.Contains("Router.replaceParams(", html);
        Assert.Contains("Router.on(applyRoute)", html);
    }

    // ── AbortController wiring (race-condition fix) ───────────────────────────

    [Fact]
    public async Task GET_root_uses_abort_controllers_for_page_and_run_detail_fetches()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("new AbortController(", html);
        Assert.Contains("function newPageController(", html);
        Assert.Contains("function newRunDetailController(", html);
        Assert.Contains("function isAbortError(", html);
    }

    // ── Toast / error feedback ────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_inlines_toast_kinds()
    {
        // Arrange — showToast accepts info/success/warning/error.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function showToast(", html);
        Assert.Contains("function showError(", html);
        Assert.Contains("function showSuccess(", html);
        Assert.Contains("toast-success", html);
        Assert.Contains("toast-warning", html);
        Assert.Contains("toast-error", html);
    }

    // ── Button busy state helper (no double-click) ────────────────────────────

    [Fact]
    public async Task GET_root_inlines_with_busy_helper()
    {
        // Arrange — withBusy disables a button + toggles aria-busy while a
        // promise is pending.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("async function withBusy(", html);
        Assert.Contains("aria-busy", html);
    }

    // ── Smart auto-refresh pause ──────────────────────────────────────────────

    [Fact]
    public async Task GET_root_pauses_auto_refresh_on_hidden_tab_and_form_focus()
    {
        // Arrange — replaces the legacy scrollTop-only heuristic.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function isAutoRefreshBlocked(", html);
        Assert.Contains("document.visibilityState === 'hidden'", html);
        Assert.Contains("visibilitychange", html);
    }

    // ── Skeleton state helpers ────────────────────────────────────────────────

    [Fact]
    public async Task GET_root_inlines_skeleton_helpers_and_css()
    {
        // Arrange — placeholders during first paint of each page.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function skeletonRows(", html);
        Assert.Contains("function skeletonCards(", html);
        Assert.Contains(".skeleton-line", html);
        Assert.Contains("@keyframes skeleton-shine", html);
    }

    // ── URL-persistent filter state ───────────────────────────────────────────

    [Fact]
    public async Task GET_root_persists_runs_filters_via_router_replace_params()
    {
        // Arrange — when the user changes a filter or pages, the URL keeps
        // flow / status / q / page so refresh + back/forward retain state.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("Router.replaceParams({ flow, status, q, page", html);
        Assert.Contains("function restoreRunsFiltersFromRoute(", html);
    }

    // ── Memory cleanup on unload ──────────────────────────────────────────────

    [Fact]
    public async Task GET_root_clears_timers_and_aborts_controllers_on_unload()
    {
        // Arrange — defends against a lingering setInterval firing after tab
        // close, and against fetch responses hitting a torn-down dispatcher.

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("'beforeunload'", html);
        Assert.Contains("clearInterval(autoRefreshTimer)", html);
    }
}
