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
    private static readonly Guid ApprovalFlowId = new("5a5a5a5a-0000-0000-0000-0000000000a1");
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    /// <summary>
    /// Registers the runs' flow with one unrestricted <c>WaitForSignal</c> step: the endpoint refuses signals
    /// for flows this host does not register (<c>UnregisteredFlowSignals = Deny</c>).
    /// </summary>
    public SignalEndpointTests()
    {
        _client = _server.CreateClient();
        var flow = Substitute.For<FlowOrchestrator.Core.Abstractions.IFlowDefinition>();
        flow.Id.Returns(ApprovalFlowId);
        flow.Manifest.Returns(new FlowOrchestrator.Core.Abstractions.FlowManifest
        {
            Steps = new FlowOrchestrator.Core.Abstractions.StepCollection
            {
                ["wait_for_approval"] = new FlowOrchestrator.Core.Abstractions.StepMetadata
                {
                    Type = "WaitForSignal",
                    Inputs = new Dictionary<string, object?> { ["signalName"] = "approval" },
                },
            },
        });
        _server.Flows = new FlowOrchestrator.Core.Abstractions.FlowRegistry([flow]);
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task POST_signal_returns_404_when_run_missing()
    {
        // Arrange
        _server.FlowRunStore.GetRunDetailAsync(Arg.Any<Guid>()).Returns(default(FlowRunRecord?));
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{Guid.NewGuid()}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_400_when_run_not_running()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = ApprovalFlowId, Status = "Succeeded" });
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_400_for_invalid_json_body()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = ApprovalFlowId, Status = "Running" });
        using var content = new StringContent("{not-json", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_404_when_no_matching_waiter()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = ApprovalFlowId, Status = "Running" });
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.NotFound, null, null)));
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_409_when_already_delivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = ApprovalFlowId, Status = "Running" });
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.AlreadyDelivered, "wait_step", DateTimeOffset.UtcNow)));
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task POST_signal_returns_200_with_step_key_when_delivered()
    {
        // Arrange
        var runId = Guid.NewGuid();
        _server.FlowRunStore.GetRunDetailAsync(runId)
            .Returns(new FlowRunRecord { Id = runId, FlowId = ApprovalFlowId, Status = "Running" });
        var deliveredAt = DateTimeOffset.UtcNow;
        _server.SignalDispatcher
            .DispatchAsync(runId, "approval", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SignalDeliveryResult>(
                new SignalDeliveryResult(SignalDeliveryStatus.Delivered, "wait_for_approval", deliveredAt)));
        using var content = new StringContent("""{"approved":true}""", Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync(
            $"/flows/api/runs/{runId}/signals/approval",
            content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"delivered\":true", body);
        Assert.Contains("wait_for_approval", body);
    }
}
