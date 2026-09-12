using System.Net;
using System.Text;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using NSubstitute;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Every trigger entry point must honour the engine's <see cref="FlowTriggerResult.Disabled"/>
/// outcome instead of reporting the run id it invented before calling the engine.
/// </summary>
/// <remarks>
/// The endpoints used to do this:
/// <code>
/// var ctx = new TriggerContext { RunId = Guid.NewGuid(), ... };
/// await engine.TriggerAsync(ctx);                       // return value discarded
/// await WriteJsonAsync(http.Response, new { runId = ctx.RunId, message = "... triggered." });
/// </code>
/// The engine's gate worked — it created nothing and returned <c>Disabled</c> — but the response
/// still carried a run id and the word "triggered". A caller then held an identifier that no run
/// backed: reading it 404s, while the caller believed a disabled flow had executed. The webhook path
/// additionally wrote an accepted-delivery log line for a delivery that never happened.
/// </remarks>
public sealed class DisabledFlowTriggerGateTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly Guid _flowId = Guid.NewGuid();

    /// <summary>Registers one flow and makes the engine answer as it does for a disabled flow.</summary>
    public DisabledFlowTriggerGateTests()
    {
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(_flowId);
        flow.Manifest.Returns(new FlowManifest
        {
            Triggers = new FlowTriggerCollection
            {
                ["manual"] = new TriggerMetadata { Type = TriggerType.Manual },
                ["webhook"] = new TriggerMetadata
                {
                    Type = TriggerType.Webhook,
                    Inputs = new Dictionary<string, object?> { ["webhookSlug"] = "disabled-sample" }
                }
            },
            Steps = new StepCollection { ["only"] = new StepMetadata { Type = "LogMessage" } }
        });

        _server.FlowRepository.GetAllFlowsAsync()
            .Returns(new ValueTask<IReadOnlyList<IFlowDefinition>>(new[] { flow }));

        _server.FlowOrchestrator.TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<object?>(new FlowTriggerResult(null, Disabled: true)));
    }

    [Fact]
    public async Task Manual_trigger_of_a_disabled_flow_reports_no_run()
    {
        // Arrange

        // Act
        var response = await _server.Client.PostAsync(
            $"/flows/api/flows/{_flowId}/trigger",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"disabled\":true", body);
        Assert.Contains("\"runId\":null", body);
        Assert.DoesNotContain("triggered.", body);
    }

    [Fact]
    public async Task Webhook_delivery_to_a_disabled_flow_reports_no_run()
    {
        // Arrange

        // Act
        var response = await _server.Client.PostAsync(
            "/flows/api/webhook/disabled-sample",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        // Assert — 202 rather than 409: a webhook sender cannot act on the distinction and retrying
        // would not help, so the delivery is acknowledged while being reported as not started.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("\"disabled\":true", body);
        Assert.Contains("\"runId\":null", body);
    }

    [Fact]
    public async Task A_run_id_is_never_reported_for_a_trigger_the_engine_refused()
    {
        // Arrange — the run id the endpoint generates before calling the engine must not leak into
        // any response, on any path, when the engine declined to start a run.
        var paths = new[]
        {
            $"/flows/api/flows/{_flowId}/trigger",
            "/flows/api/webhook/disabled-sample",
        };

        // Act
        var bodies = new List<string>();
        foreach (var path in paths)
        {
            var response = await _server.Client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        // Assert
        foreach (var body in bodies)
        {
            Assert.DoesNotContain("\"runId\":\"", body);
        }
    }

    /// <summary>Tears the test host down.</summary>
    public void Dispose() => _server.Dispose();
}
