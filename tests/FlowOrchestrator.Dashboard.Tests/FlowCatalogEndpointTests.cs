using System.Net;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

public sealed class FlowCatalogEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public FlowCatalogEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task GET_api_flows_returns_200_with_flow_list()
    {
        var flowId = Guid.NewGuid();
        _server.FlowStore.GetAllAsync().Returns([
            new FlowDefinitionRecord { Id = flowId, Name = "TestFlow", Version = "1.0", IsEnabled = true }
        ]);

        var response = await _client.GetAsync("/flows/api/flows");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("TestFlow");
    }

    [Fact]
    public async Task GET_api_flows_by_id_returns_200_for_existing_flow()
    {
        var id = Guid.NewGuid();
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, Name = "FoundFlow", Version = "1.0" });

        var response = await _client.GetAsync($"/flows/api/flows/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("FoundFlow");
    }

    [Fact]
    public async Task GET_api_flows_by_id_returns_404_for_missing_flow()
    {
        _server.FlowStore.GetByIdAsync(Arg.Any<Guid>()).Returns((FlowDefinitionRecord?)null);

        var response = await _client.GetAsync($"/flows/api/flows/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_enable_returns_200_and_calls_sync()
    {
        var id = Guid.NewGuid();
        var record = new FlowDefinitionRecord { Id = id, Name = "Flow", Version = "1.0", IsEnabled = true };
        _server.FlowStore.SetEnabledAsync(id, true).Returns(record);

        var response = await _client.PostAsync($"/flows/api/flows/{id}/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerSync.Received(1).SyncTriggers(id, true);
    }

    [Fact]
    public async Task POST_enable_returns_404_when_flow_not_found()
    {
        var id = Guid.NewGuid();
        _server.FlowStore.SetEnabledAsync(id, true).Returns<FlowDefinitionRecord>(_ => throw new KeyNotFoundException());

        var response = await _client.PostAsync($"/flows/api/flows/{id}/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_disable_returns_200_and_calls_sync()
    {
        var id = Guid.NewGuid();
        var record = new FlowDefinitionRecord { Id = id, Name = "Flow", Version = "1.0", IsEnabled = false };
        _server.FlowStore.SetEnabledAsync(id, false).Returns(record);

        var response = await _client.PostAsync($"/flows/api/flows/{id}/disable", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerSync.Received(1).SyncTriggers(id, false);
    }

    [Fact]
    public async Task POST_disable_returns_404_when_flow_not_found()
    {
        var id = Guid.NewGuid();
        _server.FlowStore.SetEnabledAsync(id, false).Returns<FlowDefinitionRecord>(_ => throw new KeyNotFoundException());

        var response = await _client.PostAsync($"/flows/api/flows/{id}/disable", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
