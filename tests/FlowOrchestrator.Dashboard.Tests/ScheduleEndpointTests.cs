using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Integration tests for the schedule management endpoints
/// (<c>GET /api/schedules</c>, <c>POST /api/schedules/{jobId}/trigger</c>,
/// <c>POST /api/schedules/{jobId}/pause</c>, <c>POST /api/schedules/{jobId}/resume</c>,
/// <c>PUT /api/schedules/{jobId}/cron</c>) and the manual flow trigger endpoint
/// (<c>POST /api/flows/{id}/trigger</c>).
///
/// These endpoints use <see cref="IRecurringTriggerDispatcher"/>, <see cref="IRecurringTriggerInspector"/>,
/// and <see cref="IFlowOrchestrator"/> — all injected as mocks through <see cref="DashboardTestServer"/>.
/// </summary>
public sealed class ScheduleEndpointTests : IDisposable
{
    private readonly DashboardTestServer _server = new();
    private readonly HttpClient _client;

    public ScheduleEndpointTests() => _client = _server.CreateClient();

    public void Dispose() => _server.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string JobId(Guid flowId, string triggerKey) =>
        $"flow-{flowId:D}-{triggerKey}";

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    // ═══════════════════════════════════════════════════════════════════════
    // GET /api/schedules
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Returns 200 with an empty array when no recurring jobs are registered.</summary>
    [Fact]
    public async Task GET_api_schedules_returns_200_with_empty_array_when_no_jobs()
    {
        _server.TriggerInspector.GetJobsAsync()
            .Returns(Array.Empty<RecurringTriggerInfo>());
        _server.FlowStore.GetAllAsync()
            .Returns(Array.Empty<FlowDefinitionRecord>());

        var response = await _client.GetAsync("/flows/api/schedules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("[");
    }

    /// <summary>
    /// Returns 200 and includes job metadata when recurring jobs exist.
    /// The response must include <c>jobId</c>, <c>cron</c>, and <c>flowName</c> fields.
    /// </summary>
    [Fact]
    public async Task GET_api_schedules_returns_jobs_with_metadata()
    {
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

        var response = await _client.GetAsync("/flows/api/schedules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("HourlyFlow");
        body.Should().Contain("0 * * * *");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // POST /api/schedules/{jobId}/trigger
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calls <see cref="IRecurringTriggerDispatcher.TriggerOnce"/> when the job is not paused,
    /// and returns 200 with a success message.
    /// </summary>
    [Fact]
    public async Task POST_schedules_trigger_calls_TriggerOnce_for_active_job()
    {
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        // No paused state — job is active.
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/trigger", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerDispatcher.Received(1).TriggerOnce(jobId);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("success");
    }

    /// <summary>
    /// Calls <see cref="IRecurringTriggerDispatcher.EnqueueTriggerAsync"/> when the job is paused,
    /// rather than <c>TriggerOnce</c> (which would fail for a removed job).
    /// </summary>
    [Fact]
    public async Task POST_schedules_trigger_calls_EnqueueTriggerAsync_for_paused_job()
    {
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

        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/trigger", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.TriggerDispatcher.Received(1)
            .EnqueueTriggerAsync(flowId, "cron", Arg.Any<CancellationToken>());
        _server.TriggerDispatcher.DidNotReceive().TriggerOnce(Arg.Any<string>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // POST /api/schedules/{jobId}/pause
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pausing a valid job calls <see cref="IRecurringTriggerDispatcher.Remove"/>
    /// and saves the paused state to the store.
    /// </summary>
    [Fact]
    public async Task POST_schedules_pause_removes_recurring_job_and_saves_state()
    {
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.FlowStore.GetByIdAsync(flowId)
            .Returns(new FlowDefinitionRecord { Id = flowId, Name = "DailyFlow", IsEnabled = true });
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/pause", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerDispatcher.Received(1).Remove(jobId);
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.JobId == jobId && s.IsPaused));
    }

    /// <summary>
    /// Returns 400 when the job ID does not follow the <c>flow-{guid}-{key}</c> format.
    /// </summary>
    [Fact]
    public async Task POST_schedules_pause_returns_400_for_invalid_job_id()
    {
        var response = await _client.PostAsync("/flows/api/schedules/not-a-valid-job/pause", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _server.TriggerDispatcher.DidNotReceive().Remove(Arg.Any<string>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // POST /api/schedules/{jobId}/resume
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resuming a paused job calls <see cref="IRecurringTriggerDispatcher.RegisterOrUpdate"/>
    /// with the stored cron override and returns 200.
    /// </summary>
    [Fact]
    public async Task POST_schedules_resume_registers_recurring_job_with_cron_override()
    {
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

        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/resume", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", cron);
    }

    /// <summary>
    /// Returns 404 when the flow referenced by the job ID is not in the store.
    /// </summary>
    [Fact]
    public async Task POST_schedules_resume_returns_404_when_flow_not_found()
    {
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.FlowStore.GetByIdAsync(flowId).Returns((FlowDefinitionRecord?)null);
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        var response = await _client.PostAsync($"/flows/api/schedules/{jobId}/resume", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _server.TriggerDispatcher.DidNotReceive()
            .RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PUT /api/schedules/{jobId}/cron
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Updating the cron expression for an active (non-paused) job calls
    /// <see cref="IRecurringTriggerDispatcher.RegisterOrUpdate"/> with the new expression.
    /// </summary>
    [Fact]
    public async Task PUT_schedules_cron_updates_expression_and_calls_RegisterOrUpdate_for_active_job()
    {
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

        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = newCron }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", newCron);
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.CronOverride == newCron));
    }

    /// <summary>
    /// Updating the cron for a paused job saves the new expression but does NOT
    /// call <see cref="IRecurringTriggerDispatcher.RegisterOrUpdate"/> (job remains removed).
    /// </summary>
    [Fact]
    public async Task PUT_schedules_cron_saves_override_without_registering_when_paused()
    {
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

        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = newCron }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _server.TriggerDispatcher.DidNotReceive()
            .RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
        await _server.ScheduleStateStore.Received(1)
            .SaveAsync(Arg.Is<FlowScheduleState>(s => s.CronOverride == newCron));
    }

    /// <summary>
    /// Returns 400 when the request body is missing the <c>cronExpression</c> field.
    /// </summary>
    [Fact]
    public async Task PUT_schedules_cron_returns_400_when_cronExpression_missing()
    {
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { other = "field" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Returns 404 when the flow cannot be found in the store.
    /// </summary>
    [Fact]
    public async Task PUT_schedules_cron_returns_404_when_flow_not_found()
    {
        var flowId = Guid.NewGuid();
        var jobId = JobId(flowId, "cron");

        _server.FlowStore.GetByIdAsync(flowId).Returns((FlowDefinitionRecord?)null);
        _server.ScheduleStateStore.GetAsync(jobId).Returns((FlowScheduleState?)null);

        var response = await _client.PutAsync(
            $"/flows/api/schedules/{jobId}/cron",
            JsonBody(new { cronExpression = "0 * * * *" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // POST /api/flows/{id}/trigger  (manual trigger endpoint)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manually triggering a registered flow calls
    /// <see cref="IFlowOrchestrator.TriggerAsync"/> and returns 200 with a <c>runId</c>.
    /// </summary>
    [Fact]
    public async Task POST_api_flows_trigger_calls_engine_TriggerAsync_and_returns_200()
    {
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });

        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(
            Arg.Is<ITriggerContext>(ctx => ctx.Flow.Id == flowId && ctx.Trigger.Key == "manual"),
            Arg.Any<CancellationToken>());
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("runId");
    }

    /// <summary>
    /// Returns 404 when the requested flow is not registered in the repository.
    /// </summary>
    [Fact]
    public async Task POST_api_flows_trigger_returns_404_when_flow_not_found()
    {
        _server.FlowRepository.GetAllFlowsAsync().Returns(Array.Empty<IFlowDefinition>());

        var response = await _client.PostAsync($"/flows/api/flows/{Guid.NewGuid()}/trigger", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await _server.FlowOrchestrator.DidNotReceive()
            .TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Returns 400 when the request body contains invalid JSON.
    /// </summary>
    [Fact]
    public async Task POST_api_flows_trigger_returns_400_for_invalid_json_body()
    {
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });

        var content = new StringContent("not-json", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Passes the JSON body to the trigger context so step handlers can access trigger data.
    /// </summary>
    [Fact]
    public async Task POST_api_flows_trigger_with_json_body_passes_trigger_data_to_engine()
    {
        var flowId = Guid.NewGuid();
        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        _server.FlowRepository.GetAllFlowsAsync().Returns(new[] { flow });

        var content = new StringContent("""{"orderId":42}""", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"/flows/api/flows/{flowId}/trigger", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _server.FlowOrchestrator.Received(1).TriggerAsync(
            Arg.Is<ITriggerContext>(ctx => ctx.Trigger.Data != null),
            Arg.Any<CancellationToken>());
    }
}
