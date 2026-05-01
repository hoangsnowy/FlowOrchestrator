using System.Net;
using System.Text;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration coverage for <c>POST /flows/api/runs/{runId}/signals/{signalName}</c>:
/// status code matrix (404 missing run, 404 no waiter, 409 already delivered, 400 bad JSON, 200 delivered)
/// plus the run-status precondition.
/// </summary>
public sealed class SignalEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public SignalEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task POST_signal_returns_404_when_run_missing()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns((FlowRunRecord?)null);

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{Guid.NewGuid()}/signals/approval",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_400_when_run_not_running()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Succeeded" });

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_400_for_invalid_json_body()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            new StringContent("{not-json", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_404_when_no_matching_waiter()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null)));

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_409_when_already_delivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, "wait_step", DateTimeOffset.UtcNow)));

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_200_with_step_key_when_delivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = Guid.NewGuid(), Status = "Running" });
        var deliveredAt = DateTimeOffset.UtcNow;
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.Delivered, "wait_for_approval", deliveredAt)));

        // Act
        var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            new StringContent("""{"approved":true}""", Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"delivered\":true", body);
        Assert.Contains("wait_for_approval", body);
    }
}
