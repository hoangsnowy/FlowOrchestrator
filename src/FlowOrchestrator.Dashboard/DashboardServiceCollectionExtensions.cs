using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    public static IServiceCollection AddFlowDashboard(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapFlowDashboard(this IEndpointRouteBuilder endpoints, string basePath = "/flows")
    {
        var html = DashboardHtml.Render(basePath);

        endpoints.MapGet(basePath, (HttpContext http) =>
        {
            http.Response.ContentType = "text/html; charset=utf-8";
            return http.Response.WriteAsync(html);
        });

        // ── Flow catalog endpoints ──

        endpoints.MapGet($"{basePath}/api/flows", async (HttpContext http, IFlowStore store) =>
        {
            var flows = await store.GetAllAsync();
            await http.Response.WriteAsJsonAsync(flows);
        });

        endpoints.MapGet($"{basePath}/api/flows/{{id:guid}}", async (HttpContext http, IFlowStore store, Guid id) =>
        {
            var flow = await store.GetByIdAsync(id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(flow);
        });

        endpoints.MapPost($"{basePath}/api/flows/{{id:guid}}/enable", async (HttpContext http, IFlowStore store, Guid id) =>
        {
            try
            {
                var flow = await store.SetEnabledAsync(id, true);
                await http.Response.WriteAsJsonAsync(flow);
            }
            catch (KeyNotFoundException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        endpoints.MapPost($"{basePath}/api/flows/{{id:guid}}/disable", async (HttpContext http, IFlowStore store, Guid id) =>
        {
            try
            {
                var flow = await store.SetEnabledAsync(id, false);
                await http.Response.WriteAsJsonAsync(flow);
            }
            catch (KeyNotFoundException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        endpoints.MapPost($"{basePath}/api/flows/{{id:guid}}/trigger", async (HttpContext http, IFlowRepository repo, Guid id) =>
        {
            var flows = await repo.GetAllFlowsAsync();
            var flow = flows.FirstOrDefault(f => f.Id == id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Flow not found in repository." });
                return;
            }

            object? body = null;
            if (http.Request.ContentLength > 0)
            {
                body = await JsonSerializer.DeserializeAsync<object>(http.Request.Body);
            }

            var ctx = new TriggerContext
            {
                RunId = Guid.NewGuid(),
                Flow = flow,
                Trigger = new Trigger("manual", "Manual", body)
            };
            BackgroundJob.Enqueue<IHangfireFlowTrigger>(t => t.TriggerAsync(ctx, null));
            await http.Response.WriteAsJsonAsync(new { runId = ctx.RunId, message = $"Flow '{flow.GetType().Name}' triggered." });
        });

        endpoints.MapGet($"{basePath}/api/handlers", (HttpContext http, IEnumerable<IStepHandlerMetadata> handlers) =>
        {
            var list = handlers.Select(h => new { type = h.Type }).ToList();
            return http.Response.WriteAsJsonAsync(list);
        });

        // ── Run monitoring endpoints ──

        endpoints.MapGet($"{basePath}/api/runs", async (HttpContext http, IFlowRunStore store) =>
        {
            var query = http.Request.Query;
            Guid? flowId = query.TryGetValue("flowId", out var flowIdValues) && Guid.TryParse(flowIdValues, out var fid) ? fid : null;
            int skip = query.TryGetValue("skip", out var skipValues) && int.TryParse(skipValues, out var s) ? s : 0;
            int take = query.TryGetValue("take", out var takeValues) && int.TryParse(takeValues, out var t) ? t : 50;

            var runs = await store.GetRunsAsync(flowId, skip, take);
            await http.Response.WriteAsJsonAsync(runs);
        });

        endpoints.MapGet($"{basePath}/api/runs/active", async (HttpContext http, IFlowRunStore store) =>
        {
            var runs = await store.GetActiveRunsAsync();
            await http.Response.WriteAsJsonAsync(runs);
        });

        endpoints.MapGet($"{basePath}/api/runs/stats", async (HttpContext http, IFlowRunStore store) =>
        {
            var stats = await store.GetStatisticsAsync();
            await http.Response.WriteAsJsonAsync(stats);
        });

        endpoints.MapGet($"{basePath}/api/runs/{{id:guid}}", async (HttpContext http, IFlowRunStore store, Guid id) =>
        {
            var run = await store.GetRunDetailAsync(id);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(run);
        });

        endpoints.MapGet($"{basePath}/api/runs/{{id:guid}}/steps", async (HttpContext http, IFlowRunStore store, Guid id) =>
        {
            var run = await store.GetRunDetailAsync(id);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(run.Steps ?? []);
        });

        return endpoints;
    }
}
