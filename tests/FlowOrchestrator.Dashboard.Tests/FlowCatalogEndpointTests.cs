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
        // Arrange
        var flowId = Guid.NewGuid();
        _server.FlowStore.GetAllAsync().Returns([
            new FlowDefinitionRecord { Id = flowId, Name = "TestFlow", Version = "1.0", IsEnabled = true }
        ]);

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TestFlow", body);
    }

    [Fact]
    public async Task GET_api_flows_by_id_returns_200_for_existing_flow()
    {
        // Arrange
        var id = Guid.NewGuid();
        _server.FlowStore.GetByIdAsync(id).Returns(new FlowDefinitionRecord { Id = id, Name = "FoundFlow", Version = "1.0" });

        // Act
        var response = await _client.GetAsync($"/flows/api/flows/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("FoundFlow", body);
    }

    [Fact]
    public async Task GET_api_flows_by_id_returns_404_for_missing_flow()
    {
        // Arrange
        _server.FlowStore.GetByIdAsync(Arg.Any<Guid>()).Returns((FlowDefinitionRecord?)null);

        // Act
        var response = await _client.GetAsync($"/flows/api/flows/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_enable_returns_200_and_calls_sync()
    {
        // Arrange
        var id = Guid.NewGuid();
        var record = new FlowDefinitionRecord { Id = id, Name = "Flow", Version = "1.0", IsEnabled = true };
        _server.FlowStore.SetEnabledAsync(id, true).Returns(record);

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{id}/enable", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerSync.Received(1).SyncTriggers(id, true);
    }

    [Fact]
    public async Task POST_enable_returns_404_when_flow_not_found()
    {
        // Arrange
        var id = Guid.NewGuid();
        _server.FlowStore.SetEnabledAsync(id, true).Returns<FlowDefinitionRecord>(_ => throw new KeyNotFoundException());

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{id}/enable", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_disable_returns_200_and_calls_sync()
    {
        // Arrange
        var id = Guid.NewGuid();
        var record = new FlowDefinitionRecord { Id = id, Name = "Flow", Version = "1.0", IsEnabled = false };
        _server.FlowStore.SetEnabledAsync(id, false).Returns(record);

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{id}/disable", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerSync.Received(1).SyncTriggers(id, false);
    }

    [Fact]
    public async Task POST_disable_returns_404_when_flow_not_found()
    {
        // Arrange
        var id = Guid.NewGuid();
        _server.FlowStore.SetEnabledAsync(id, false).Returns<FlowDefinitionRecord>(_ => throw new KeyNotFoundException());

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{id}/disable", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
