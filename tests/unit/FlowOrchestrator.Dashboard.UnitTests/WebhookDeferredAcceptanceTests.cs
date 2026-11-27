using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Dashboard.Webhooks;
using FlowOrchestrator.Dashboard.Webhooks.Dlq;
using FlowOrchestrator.Dashboard.Webhooks.Signature;
using Microsoft.AspNetCore.Http;

namespace FlowOrchestrator.Dashboard.UnitTests;

/// <summary>
/// Contract of <see cref="WebhookSecurityPipeline.EvaluateDeferringAcceptanceAsync"/>: an accepted request is
/// logged only once the caller commits it, and a caller-side refusal is logged as a rejection instead.
/// </summary>
public sealed class WebhookDeferredAcceptanceTests
{
    private static readonly IReadOnlyDictionary<string, object?> NoSchemeInputs = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static (WebhookSecurityPipeline Pipeline, InMemoryWebhookRejectionStore Log) Create()
    {
        var log = new InMemoryWebhookRejectionStore();
        var pipeline = new WebhookSecurityPipeline(
            new HmacSignatureVerifier(),
            new WebhookSecurityOptions { EnforcementMode = WebhookEnforcementMode.Enforce },
            rejectionStore: log);
        return (pipeline, log);
    }

    private static async Task<IReadOnlyList<WebhookRejectionRecord>> RowsAsync(InMemoryWebhookRejectionStore log)
        => (await log.QueryAsync(new WebhookRejectionQuery(null, null, null, IncludeAccepted: true, IncludeRejected: true, Skip: 0, Take: 50))).Items;

    [Fact]
    public async Task Deferred_acceptance_is_not_logged_until_committed()
    {
        // Arrange
        var (pipeline, log) = Create();
        var acceptance = new WebhookSecurityPipeline.DeferredAcceptance();

        // Act
        var verdict = await pipeline.EvaluateDeferringAcceptanceAsync(new DefaultHttpContext(), Guid.NewGuid(), "hook", NoSchemeInputs, ReadOnlyMemory<byte>.Empty, NoHeaders, formFields: null, acceptance);
        var beforeCommit = await RowsAsync(log);
        pipeline.CommitAcceptance(acceptance);
        pipeline.CommitAcceptance(acceptance);
        var afterCommit = await RowsAsync(log);

        // Assert
        Assert.Equal(WebhookSecurityPipeline.Decision.Accept, verdict.Decision);
        Assert.Empty(beforeCommit);
        var row = Assert.Single(afterCommit);
        Assert.True(row.IsAccepted);
    }

    [Fact]
    public async Task Refusal_after_the_pipeline_replaces_the_held_acceptance_with_a_rejection()
    {
        // Arrange
        var (pipeline, log) = Create();
        var http = new DefaultHttpContext();
        var flowId = Guid.NewGuid();
        var acceptance = new WebhookSecurityPipeline.DeferredAcceptance();
        await pipeline.EvaluateDeferringAcceptanceAsync(http, flowId, "hook", NoSchemeInputs, "{}"u8.ToArray(), NoHeaders, formFields: null, acceptance);

        // Act
        await pipeline.RecordRejectionAfterPipelineAsync(http, flowId, "hook", "secret_invalid", StatusCodes.Status401Unauthorized, "{}"u8.ToArray(), NoHeaders, acceptance);
        pipeline.CommitAcceptance(acceptance);
        var rows = await RowsAsync(log);

        // Assert
        var row = Assert.Single(rows);
        Assert.False(row.IsAccepted);
        Assert.Equal(StatusCodes.Status401Unauthorized, row.StatusCode);
        Assert.Equal("secret_invalid", row.Reason);
        Assert.Equal(flowId, row.FlowId);
    }

    [Fact]
    public async Task Public_evaluate_still_logs_an_accepted_request_immediately()
    {
        // Arrange
        var (pipeline, log) = Create();

        // Act
        await pipeline.EvaluateAsync(new DefaultHttpContext(), Guid.NewGuid(), "hook", NoSchemeInputs, ReadOnlyMemory<byte>.Empty, NoHeaders);
        var rows = await RowsAsync(log);

        // Assert
        Assert.True(Assert.Single(rows).IsAccepted);
    }
}
