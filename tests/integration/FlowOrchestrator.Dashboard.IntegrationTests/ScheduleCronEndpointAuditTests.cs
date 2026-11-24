using System.Net;
using System.Text;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// <c>PUT /api/schedules/{jobId}/cron</c> input handling: invalid expressions, malformed bodies and the body cap.
/// Copied from the 4th-sweep dashboard audit (<c>DashboardSurfaceAuditTests</c>); every test failed before the fix.
/// </summary>
public sealed class ScheduleCronEndpointAuditTests
{
    /// <summary>
    /// STUCK SCHEDULE. <c>PUT /api/schedules/{jobId}/cron</c> persists the override to the schedule
    /// state store BEFORE asking the runtime to register it. Every runtime rejects an invalid
    /// expression in <c>RegisterOrUpdate</c> (Cronos / Hangfire throw), so the request 500s with the
    /// bad expression already saved; a later Resume reads it back and fails the same way, until an
    /// operator happens to PUT a valid one.
    /// </summary>
    [Fact]
    public async Task Cron_update_with_invalid_expression_is_rejected_and_not_persisted()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "cron");
        server.FlowStore.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord { Id = flowId, Name = "F", ManifestJson = "{}" });
        server.TriggerDispatcher
            .When(d => d.RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), "not a cron"))
            .Do(_ => throw new ArgumentException("CRON expression is invalid."));
        using var content = new StringContent("{\"cronExpression\":\"not a cron\"}", Encoding.UTF8, "application/json");

        // Act
        // An exception escaping the endpoint fails the test on this call, which is the intended failure.
        using var response = await server.Client.PutAsync($"/flows/api/schedules/{jobId}/cron", content);
        var status = response.StatusCode;

        // Assert — Expected: nothing saved and 400. Actual: the invalid override is persisted, then 500.
        await server.ScheduleStateStore.DidNotReceive().SaveAsync(Arg.Is<FlowScheduleState>(s => s.CronOverride == "not a cron"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// Resume of a schedule whose persisted override does not parse (written by a version that did not validate
    /// it) falls back to the manifest cron and clears the override, instead of answering 500 on every attempt.
    /// </summary>
    [Fact]
    public async Task Resume_with_an_unparseable_persisted_override_falls_back_to_the_manifest_cron()
    {
        // Arrange
        using var server = new DashboardTestServer();
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "cron");
        server.FlowStore.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord
        {
            Id = flowId,
            Name = "F",
            ManifestJson = "{\"triggers\":{\"cron\":{\"type\":\"Cron\",\"inputs\":{\"cronExpression\":\"0 * * * *\"}}}}"
        });
        server.ScheduleStateStore.GetAsync(jobId).Returns(new FlowScheduleState
        {
            JobId = jobId, FlowId = flowId, FlowName = "F", TriggerKey = "cron", IsPaused = true, CronOverride = "not a cron"
        });
        server.TriggerDispatcher
            .When(d => d.RegisterOrUpdate(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), "not a cron"))
            .Do(_ => throw new ArgumentException("CRON expression is invalid."));

        // Act
        // An exception escaping the endpoint fails the test on this call, which is the intended failure.
        using var response = await server.Client.PostAsync($"/flows/api/schedules/{jobId}/resume", content: null);
        var status = response.StatusCode;

        // Assert — resumed on the manifest cron; the bad override is cleared.
        Assert.Equal(HttpStatusCode.OK, status);
        server.TriggerDispatcher.Received(1).RegisterOrUpdate(jobId, flowId, "cron", "0 * * * *");
        await server.ScheduleStateStore.Received(1).SaveAsync(Arg.Is<FlowScheduleState>(s => !s.IsPaused && s.CronOverride == null));
    }

    /// <summary>
    /// 500 ON MALFORMED INPUT. The cron endpoint calls <c>TryGetProperty</c> / <c>GetString</c> on
    /// whatever JSON arrives: a non-object body or a non-string <c>cronExpression</c> throws
    /// <see cref="InvalidOperationException"/>, and malformed JSON throws <c>JsonException</c>.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"cronExpression\":5}")]
    [InlineData("{not json")]
    public async Task Cron_update_with_malformed_body_returns_400(string json)
    {
        // Arrange
        using var server = new DashboardTestServer();
        var jobId = RecurringTriggerJobId.Create(Guid.NewGuid(), "cron");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        // An exception escaping the endpoint fails the test on this call, which is the intended failure.
        using var response = await server.Client.PutAsync($"/flows/api/schedules/{jobId}/cron", content);
        var status = response.StatusCode;

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    /// <summary>
    /// CAP BYPASS. The cron endpoint enforces <c>MaxBodyBytes</c> only through the declared
    /// <c>Content-Length</c>; a chunked body (no Content-Length) is deserialised without any cap. The
    /// manual-trigger endpoint streams with a cap and rejects the same body with 413.
    /// </summary>
    [Fact]
    public async Task Cron_update_chunked_body_over_cap_returns_413()
    {
        // Arrange
        using var server = new DashboardTestServer(o => o.UseWebhookSecurity(s => s.UseMaxBodyBytes(64)));
        var flowId = Guid.NewGuid();
        var jobId = RecurringTriggerJobId.Create(flowId, "cron");
        server.FlowStore.GetByIdAsync(flowId).Returns(new FlowDefinitionRecord { Id = flowId, Name = "F", ManifestJson = "{}" });
        var json = "{\"cronExpression\":\"*/5 * * * *\",\"pad\":\"" + new string('x', 4096) + "\"}";
        using var content = new UnknownLengthContent(Encoding.UTF8.GetBytes(json));

        // Act
        using var response = await server.Client.PutAsync($"/flows/api/schedules/{jobId}/cron", content);

        // Assert — Expected 413. Actual 200 (4 KiB body accepted against a 64-byte cap).
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
