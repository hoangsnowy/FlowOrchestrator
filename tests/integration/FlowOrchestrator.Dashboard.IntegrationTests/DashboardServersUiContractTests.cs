using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down the Servers tab surface in the served dashboard bundle — nav entry, page section ids, the loader and
/// its endpoint URL, the refresh dispatcher route, the status chip classes, and the per-attempt "ran on" line —
/// so a later edit that renames a load-bearing id or function fails here.
/// </summary>
public sealed class DashboardServersUiContractTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly Lazy<Task<string>> _body;

    public DashboardServersUiContractTests()
    {
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        var client = _server.CreateClient();
        _body = new Lazy<Task<string>>(() => client.GetStringAsync("/flows"));
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Sidebar_contains_the_servers_nav_entry()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("data-page=\"servers\"", html, StringComparison.Ordinal);
        Assert.Contains("navigate('servers')", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servers_page_section_is_inlined_with_known_ids()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("id=\"page-servers\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"servers-table\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"servers-count-label\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"servers-note\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hamburger-servers\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_endpoint_and_refresh_route_are_inlined()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function loadServers(", html, StringComparison.Ordinal);
        Assert.Contains("function renderServersTable(", html, StringComparison.Ordinal);
        Assert.Contains("BASE + '/servers'", html, StringComparison.Ordinal);
        Assert.Contains("currentPage === 'servers'", html, StringComparison.Ordinal);
        Assert.Contains("'scheduled','servers'", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_untracked_and_status_states_reuse_the_shared_classes()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("does not track servers", html, StringComparison.Ordinal);
        Assert.Contains("No server has reported a heartbeat yet.", html, StringComparison.Ordinal);
        Assert.Contains("class=\"recent-table servers-table\"", html, StringComparison.Ordinal);
        Assert.Contains("skeletonRows(3, 9)", html, StringComparison.Ordinal);
        Assert.Contains(".chip--server-active{background:var(--fo-success-bg)", html, StringComparison.Ordinal);
        Assert.Contains(".chip--server-stale{background:var(--fo-warn-bg)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_detail_shows_the_server_of_each_attempt()
    {
        // Arrange

        // Act
        var html = await _body.Value;

        // Assert
        Assert.Contains("function renderAttemptServers(", html, StringComparison.Ordinal);
        Assert.Contains("renderAttemptServers(step) + errorPanel", html, StringComparison.Ordinal);
        Assert.Contains("a.serverId", html, StringComparison.Ordinal);
    }
}
