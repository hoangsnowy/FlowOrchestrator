using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowOrchestrator.Dashboard.Tests;

/// <summary>
/// Locks down what an anonymous caller may do on a dashboard with neither Basic Auth nor identity-based
/// sign-in configured: operator access in Development, read-only with secrets masked everywhere else,
/// operator access again when the host opts in with <see cref="FlowDashboardOptions.AllowAnonymousOperators"/>,
/// and a startup log line that says which of the three applies.
/// </summary>
public sealed class DashboardAnonymousAccessTests
{
    private const string Secret = "s3cr3t-value";

    private static FlowDefinitionRecord ArrangeFlowWithSecret(DashboardTestServer server)
    {
        var flowId = Guid.NewGuid();
        var record = new FlowDefinitionRecord
        {
            Id = flowId,
            Name = "Orders",
            ManifestJson = "{\"triggers\":{\"hook\":{\"type\":\"Webhook\",\"inputs\":{\"webhookSecret\":\"" + Secret + "\"}}}}",
        };
        server.FlowStore.GetAllAsync().Returns([record]);
        server.FlowStore.GetByIdAsync(flowId).Returns(record);

        var flow = Substitute.For<IFlowDefinition>();
        flow.Id.Returns(flowId);
        flow.Manifest.Returns(new FlowManifest());
        server.Flows = new FlowRegistry([flow]);
        return record;
    }

    private static async Task<bool> CanOperateAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/flows/api/me"));
        Assert.Equal("none", doc.RootElement.GetProperty("method").GetString());
        return doc.RootElement.GetProperty("canOperate").GetBoolean();
    }

    [Fact]
    public async Task Unprotected_outside_development_is_read_only_with_secrets_masked()
    {
        // Arrange
        using var server = DashboardTestServer.InEnvironment(Environments.Production);
        var flow = ArrangeFlowWithSecret(server);

        // Act
        var trigger = await server.Client.PostAsync($"/flows/api/flows/{flow.Id}/trigger", null);
        var triggerBody = await trigger.Content.ReadAsStringAsync();
        var list = await server.Client.GetStringAsync("/flows/api/flows");
        var one = await server.Client.GetStringAsync($"/flows/api/flows/{flow.Id}");
        var canOperate = await CanOperateAsync(server.Client);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
        Assert.Contains("read-only", triggerBody, StringComparison.Ordinal);
        Assert.Contains("AllowAnonymousOperators", triggerBody, StringComparison.Ordinal);
        await server.FlowOrchestrator.DidNotReceive().TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(Secret, list, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, one, StringComparison.Ordinal);
        Assert.Contains("redacted", one, StringComparison.Ordinal);
        Assert.False(canOperate);
    }

    [Theory]
    [InlineData("POST", "/flows/api/flows/{0}/enable")]
    [InlineData("POST", "/flows/api/flows/{0}/disable")]
    [InlineData("POST", "/flows/api/runs/{0}/cancel")]
    [InlineData("POST", "/flows/api/runs/{0}/rerun")]
    [InlineData("POST", "/flows/api/runs/{0}/steps/step1/retry")]
    [InlineData("POST", "/flows/api/runs/{0}/signals/approved")]
    [InlineData("POST", "/flows/api/schedules/job-1/trigger")]
    [InlineData("PUT", "/flows/api/schedules/job-1/cron")]
    public async Task Unprotected_outside_development_refuses_every_state_changing_endpoint(string method, string pathFormat)
    {
        // Arrange
        using var server = DashboardTestServer.InEnvironment(Environments.Staging);
        using var request = new HttpRequestMessage(new HttpMethod(method), string.Format(System.Globalization.CultureInfo.InvariantCulture, pathFormat, Guid.NewGuid()))
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unprotected_in_development_keeps_full_operator_access()
    {
        // Arrange
        using var server = DashboardTestServer.InEnvironment(Environments.Development);
        var flow = ArrangeFlowWithSecret(server);

        // Act
        var trigger = await server.Client.PostAsync($"/flows/api/flows/{flow.Id}/trigger", null);
        var one = await server.Client.GetStringAsync($"/flows/api/flows/{flow.Id}");
        var canOperate = await CanOperateAsync(server.Client);

        // Assert
        Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        Assert.Contains(Secret, one, StringComparison.Ordinal);
        Assert.True(canOperate);
    }

    [Fact]
    public async Task AllowAnonymousOperators_restores_operator_access_outside_development()
    {
        // Arrange
        using var server = DashboardTestServer.InEnvironment(Environments.Production, o => o.AllowAnonymousOperators = true);
        var flow = ArrangeFlowWithSecret(server);

        // Act
        var trigger = await server.Client.PostAsync($"/flows/api/flows/{flow.Id}/trigger", null);
        var one = await server.Client.GetStringAsync($"/flows/api/flows/{flow.Id}");
        var canOperate = await CanOperateAsync(server.Client);

        // Assert
        Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
        await server.FlowOrchestrator.Received(1).TriggerAsync(Arg.Any<ITriggerContext>(), Arg.Any<CancellationToken>());
        Assert.Contains(Secret, one, StringComparison.Ordinal);
        Assert.True(canOperate);
    }

    [Fact]
    public async Task AllowAnonymousOperators_binds_from_configuration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FlowDashboard:AllowAnonymousOperators"] = "true" })
            .Build();
        using var server = DashboardTestServer.WithServices(
            s => s.AddFlowDashboard(configuration, _ => { }),
            Environments.Production);

        // Act
        var canOperate = await CanOperateAsync(server.Client);

        // Assert
        Assert.True(server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FlowDashboardOptions>>().Value.AllowAnonymousOperators);
        Assert.True(canOperate);
    }

    [Fact]
    public async Task Configured_authentication_is_unaffected_by_the_environment()
    {
        // Arrange — Basic Auth outside Development still grants full access to valid credentials.
        using var server = DashboardTestServer.InEnvironment(Environments.Production, o =>
        {
            o.BasicAuth.Username = "admin";
            o.BasicAuth.Password = "correct-horse-battery";
        });
        var flow = ArrangeFlowWithSecret(server);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/flows/api/flows/{flow.Id}/trigger");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("admin:correct-horse-battery")));

        // Act
        var response = await server.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Production", false, 4103, LogLevel.Error)]
    [InlineData("Production", true, 4104, LogLevel.Error)]
    [InlineData("Development", false, 4105, LogLevel.Warning)]
    public void Startup_logs_how_an_unprotected_dashboard_is_exposed(string environment, bool allowAnonymousOperators, int eventId, LogLevel level)
    {
        // Arrange
        var logs = new CapturingLoggerProvider();

        // Act
        using var server = DashboardTestServer.WithServices(
            s =>
            {
                s.AddSingleton<ILoggerProvider>(logs);
                s.AddFlowDashboard(o => o.AllowAnonymousOperators = allowAnonymousOperators);
            },
            environment);

        // Assert
        var entry = Assert.Single(logs.Entries, e => e.Category == "FlowOrchestrator.Dashboard.Auth");
        Assert.Equal(eventId, entry.EventId.Id);
        Assert.Equal(level, entry.Level);
    }

    [Fact]
    public void Startup_logs_nothing_about_an_unprotected_dashboard_when_authentication_is_configured()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();

        // Act
        using var server = DashboardTestServer.WithServices(
            s =>
            {
                s.AddSingleton<ILoggerProvider>(logs);
                s.AddFlowDashboard(o =>
                {
                    o.BasicAuth.Username = "admin";
                    o.BasicAuth.Password = "correct-horse-battery";
                });
            },
            Environments.Development);

        // Assert
        Assert.DoesNotContain(logs.Entries, e => e.EventId.Id is 4103 or 4104 or 4105);
    }

    private sealed record LogEntry(string Category, EventId EventId, LogLevel Level);

    /// <summary>Records every log entry written through the host's logger factory.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(category, eventId, logLevel));
        }
    }
}
