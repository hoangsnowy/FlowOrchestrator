using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the v1.25 Webhooks tab surface in the served dashboard bundle:
/// nav entry, page section, JS loaders, REST endpoint URLs, and chip CSS classes.
/// These are content-level checks against the served HTML / inlined assets —
/// they catch accidental regressions when later edits rename load-bearing IDs.
/// </summary>
public sealed class DashboardWebhooksUiContractTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;
    private readonly Lazy<Task<string>> _body;

    public DashboardWebhooksUiContractTests()
    {
        _client = _server.CreateClient();
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _body = new Lazy<Task<string>>(() => _client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Sidebar_contains_webhooks_nav_entry()
    {
        var html = await _body.Value;
        Assert.Contains("data-page=\"webhooks\"", html, StringComparison.Ordinal);
        Assert.Contains("navigate('webhooks')", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_section_for_webhooks_is_inlined_with_known_ids()
    {
        var html = await _body.Value;
        Assert.Contains("id=\"page-webhooks\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"webhook-table\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"webhook-stats\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"webhook-rejected-only\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadWebhooks_function_and_endpoint_urls_are_inlined()
    {
        var html = await _body.Value;
        Assert.Contains("function loadWebhooks(", html, StringComparison.Ordinal);
        Assert.Contains("/webhooks/recent", html, StringComparison.Ordinal);
        Assert.Contains("/webhooks/stats", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_dispatcher_routes_to_loadWebhooks()
    {
        var html = await _body.Value;
        Assert.Contains("currentPage === 'webhooks'", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_input_and_pager_container_are_inlined()
    {
        var html = await _body.Value;
        Assert.Contains("id=\"webhook-search-input\"", html, StringComparison.Ordinal);
        Assert.Contains("oninput=\"onWebhookSearchInput(", html, StringComparison.Ordinal);
        Assert.Contains("id=\"webhook-pager\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pagination_helpers_and_state_are_inlined_in_js()
    {
        var html = await _body.Value;
        Assert.Contains("function setWebhookPage(", html, StringComparison.Ordinal);
        Assert.Contains("function setWebhookPageSize(", html, StringComparison.Ordinal);
        Assert.Contains("function onWebhookSearchInput(", html, StringComparison.Ordinal);
        Assert.Contains("function renderWebhookPager(", html, StringComparison.Ordinal);
        Assert.Contains("includeTotal", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chip_css_classes_are_inlined()
    {
        var html = await _body.Value;
        Assert.Contains(".chip--accept", html, StringComparison.Ordinal);
        Assert.Contains(".chip--reject", html, StringComparison.Ordinal);
        Assert.Contains(".webhook-chips", html, StringComparison.Ordinal);
    }
}
