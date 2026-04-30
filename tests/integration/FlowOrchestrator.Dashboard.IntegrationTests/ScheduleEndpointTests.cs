using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration tests for the schedule management endpoints and the manual flow trigger endpoint.
/// </summary>
public sealed class ScheduleEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public ScheduleEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    private static string JobId(Guid flowId, string triggerKey) =>
        $"flow-{flowId:D}-{triggerKey}";

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    [Fact]
    public async Task GET_api_schedules_returns_200_with_empty_array_when_no_jobs()
    {
        // Arrange
        _server.TriggerInspector.GetJobsAsync()
            .Returns(Array.Empty<RecurringTriggerInfo>());
        _server.FlowStore.GetAllAsync()
            .Returns(Array.Empty<FlowDefinitionRecord>());

        // Act
        var response = await _client.GetAsync("/flows/api/schedules");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body);
    }

    [Fact]
    public async Task GET_api_schedules_returns_jobs_with_metadata()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.TriggerInspector.GetJobsAsync().Returns(new[]
        {
            new RecurringTriggerInfo(
                Id: jobId,
                Cron: "0 * * * *",
                NextExecution: DateTime.UtcNow.AddHours(1),
                LastExecution: null,
                LastJobId: null,
                LastJobState: null,
                TimeZoneId: "UTC")
        });
        _server.FlowStore.GetAllAsync().Returns(new[]
        {
            new FlowDefinitionRecord { Id = flowId, Name = "HourlyFlow", IsEnabled = true }
        });

        // Act
        var response = await _client.GetAsync("/flows/api/schedules");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("HourlyFlow", body);
        Assert.Contains("0 * * * *", body);
    }

    [Fact]
    public async Task POST_schedules_trigger_calls_TriggerOnce_for_active_job()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        // Act
        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/trigger", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.Received(1).TriggerOnce(jobId);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("success", body);
    }

    [Fact]
    public async Task POST_schedules_trigger_calls_EnqueueTriggerAsync_for_paused_job()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.ScheduleStateStore.GetAsync(jobId)
            .Returns(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = flowId,
                TriggerKey = "cron",
                IsPaused = true,
                FlowName = "PausedFlow"
            });

        // Act
        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/trigger", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.TriggerDispatcher.Received(1)
            .EnqueueTriggerAsync(flowId, "cron", Arg.Any<CancellationToken>());
        _server.TriggerDispatcher.DidNotReceive().TriggerOnce(Arg.Any<string>());
    }

    [Fact]
    public async Task POST_schedules_pause_removes_recurring_job_and_saves_state()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord { Id = flowId, Name = "DailyFlow", IsEnabled = true });
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        // Act
        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/pause", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.Received(1).Remove(jobId);
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.JobId == jobId && s.IsPaused));
    }

    [Fact]
    public async Task POST_schedules_pause_returns_400_for_invalid_job_id()
    {
        // Arrange

        // Act
        var response = await _client.PostAsync("/flows/api/schedules/not-a-valid-job/pause", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _server.TriggerDispatcher.DidNotReceive().Remove(Arg.Any<string>());
    }

    [Fact]
    public async Task POST_schedules_resume_registers_recurring_job_with_cron_override()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        const string cron = "*/15 * * * *";
        const string manifestJson = """{"triggers":{"cron":{"inputs":{"cronExpression":"*/15 * * * *"}}}}""";

        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord
            {
                Id = flowId,
                Name = "FrequentFlow",
                IsEnabled = true,
                ManifestJson = manifestJson
            });
        _server.ScheduleStateStore.GetAsync(jobId)
            .Returns(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = flowId,
                TriggerKey = "cron",
                IsPaused = true,
                CronOverride = cron
            });

        // Act
        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/resume", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", cron);
    }

    [Fact]
    public async Task POST_schedules_resume_returns_404_when_flow_not_found()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        _server.FlowStore.GetByIdAsync(flowId).Returns((FlowDefinitionRecord?)null);
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        // Act
        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/resume", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        _server.TriggerDispatcher.DidNotReceive()
            .RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PUT_schedules_cron_updates_expression_and_calls_RegisterOrUpdate_for_active_job()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        const string newCron = "0 0 * * *";
        var manifestJson = """{"triggers":{"cron":{"inputs":{"cronExpression":"0 * * * *"}}}}""";

        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord
            {
                Id = flowId,
                Name = "NightlyFlow",
                IsEnabled = true,
                ManifestJson = manifestJson
            });
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        // Act
        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = newCron }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", newCron);
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.CronOverride == newCron));
    }

    [Fact]
    public async Task PUT_schedules_cron_saves_override_without_registering_when_paused()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        const string newCron = "0 12 * * 1";
        var manifestJson = """{"triggers":{"cron":{"inputs":{"cronExpression":"0 * * * *"}}}}""";

        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord
            {
                Id = flowId,
                Name = "WeeklyFlow",
                IsEnabled = true,
                ManifestJson = manifestJson
            });
        _server.ScheduleStateStore.GetAsync(jobId)
            .Returns(new FlowScheduleState { JobId = jobId, FlowId = flowId, TriggerKey = "cron", IsPaused = true });

        // Act
        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = newCron }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _server.TriggerDispatcher.DidNotReceive()
            .RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.CronOverride == newCron));
    }

    [Fact]
    public async Task PUT_schedules_cron_returns_400_when_cronExpression_missing()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        // Act
        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { other = "field" }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PUT_schedules_cron_returns_404_when_flow_not_found()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");
        _server.FlowStore.GetByIdAsync(flowId).Returns((FlowDefinitionRecord?)null);
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        // Act
        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = "0 * * * *" }));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_api_flows_trigger_calls_engine_TriggerAsync_and_returns_200()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(
            Arg.Is<ITriggerContext>(ctx => ctx.Flow.Id == flowId && ctx.Trigger.Key == "manual"),
            Arg.Any<CancellationToken>());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("runId", body);
    }

    [Fact]
    public async Task POST_api_flows_trigger_returns_404_when_flow_not_found()
    {
        // Arrange
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{Guid.NewGuid()}/trigger", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await _server.FlowOrchestrator.DidNotReceive()
            .TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task POST_api_flows_trigger_returns_400_for_invalid_json_body()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        var content = new StringContent("not-json", Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_api_flows_trigger_with_json_body_passes_trigger_data_to_engine()
    {
        // Arrange
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });
        var content = new StringContent("""{"orderId":42}""", Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(
            Arg.Is<ITriggerContext>(ctx => ctx.Trigger.Data != null),
            Arg.Any<CancellationToken>());
    }
}
