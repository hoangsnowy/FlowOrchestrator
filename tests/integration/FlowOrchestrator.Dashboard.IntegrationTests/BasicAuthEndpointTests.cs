using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

public sealed class BasicAuthEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server;
    private readonly HttpClient _client;

    public BasicAuthEndpointTests()
    {
        _server = new DashboardTestServer(opts =>
        {
            opts.BasicAuth.Username = "admin";
            opts.BasicAuth.Password = "secret123";
        });
        _server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        _client = _server.CreateClient();
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task GET_flows_without_auth_header_returns_401()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_flows_with_correct_credentials_returns_200()
    {
        // Arrange
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret123")));

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_flows_with_wrong_password_returns_401()
    {
        // Arrange
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrongpassword")));

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_flows_with_wrong_username_returns_401()
    {
        // Arrange
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("hacker:secret123")));

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_flows_with_invalid_base64_returns_401()
    {
        // Arrange
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Basic not-valid-base64!!!");

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_response_includes_WWWAuthenticate_header()
    {
        // Arrange

        // Act
        var response = await _client.GetAsync("/flows/api/flows");

        // Assert
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task No_auth_required_when_credentials_not_configured()
    {
        // Arrange
        using var server = new DashboardTestServer();
        server.FlowStore.GetAllAsync().Returns(Array.Empty<FlowDefinitionRecord>());
        using var client = server.CreateClient();

        // Act
        var response = await client.GetAsync("/flows/api/flows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
