using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Diagnostics;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Observability;
using FlowOrchestrator.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FlowOrchestrator.Dashboard;

/// <summary>
/// Extension methods for registering the FlowOrchestrator dashboard services and mapping
/// the dashboard SPA, REST API, and webhook endpoints onto an <see cref="IEndpointRouteBuilder"/>.
/// </summary>
public static class DashboardServiceCollectionExtensions
{
    // Headers excluded from trigger capture: sensitive auth/session headers and low-level transport headers
    private static readonly HashSet<string> _sensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie",
        "X-Webhook-Key", "Proxy-Authorization",
        "Connection", "Transfer-Encoding", "Upgrade", "Content-Length"
    };

    /// <summary>
    /// Registers core dashboard services with default options.
    /// </summary>
    public static IServiceCollection AddFlowDashboard(this IServiceCollection services)
    {
        services.AddOptions<FlowDashboardOptions>();
        services.TryAddSingleton<IFlowScheduleStateStore, InMemoryScheduleStateStore>();
        return services;
    }

    /// <summary>
    /// Registers dashboard services and binds <see cref="FlowDashboardOptions"/> from
    /// <paramref name="configuration"/> under the <paramref name="sectionName"/> key.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">Application configuration used to bind dashboard options.</param>
    /// <param name="sectionName">Configuration section name; defaults to <see cref="FlowDashboardOptions.DefaultSectionName"/>.</param>
    public static IServiceCollection AddFlowDashboard(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = FlowDashboardOptions.DefaultSectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFlowDashboard();
        services.Configure<FlowDashboardOptions>(configuration.GetSection(sectionName));
        return services;
    }

    /// <summary>
    /// Registers dashboard services and configures <see cref="FlowDashboardOptions"/> via the
    /// provided <paramref name="configureOptions"/> delegate.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configureOptions">Delegate that configures dashboard options inline.</param>
    public static IServiceCollection AddFlowDashboard(
        this IServiceCollection services,
        Action<FlowDashboardOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddFlowDashboard();
        services.Configure(configureOptions);
        return services;
    }

    /// <summary>
    /// Maps the FlowOrchestrator dashboard SPA, REST API endpoints, and webhook receiver
    /// onto <paramref name="endpoints"/> under <paramref name="basePath"/>.
    /// Optionally applies Basic Auth if configured in <see cref="FlowDashboardOptions.BasicAuth"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to register routes on.</param>
    /// <param name="basePath">URL prefix for all dashboard routes; defaults to <c>/flows</c>.</param>
    public static IEndpointRouteBuilder MapFlowDashboard(this IEndpointRouteBuilder endpoints, string basePath = "/flows")
    {
        var options = endpoints.ServiceProvider
                          .GetService<IOptions<FlowDashboardOptions>>()?.Value
                      ?? new FlowDashboardOptions();
        var group = endpoints.MapGroup(basePath);

        if (options.BasicAuth.IsEnabled)
        {
            group.AddEndpointFilter(new FlowDashboardBasicAuthFilter(options.BasicAuth));
        }

        // Render once at startup and cache the page in three pre-compressed forms
        // (raw / Brotli / Gzip). The MapGet handler picks the best available
        // encoding for each client, writing the matching byte buffer directly to
        // the response stream — no per-request CPU spent on serialization or
        // compression. The shape stays identical to the previous "render once"
        // behaviour: still a single HTTP roundtrip, still no static-file pipeline.
        var page = DashboardHtml.RenderPrecompressed(basePath, options.Branding);

        group.MapGet("", (HttpContext http) => WriteCompressedHtmlAsync(http, page));

        // ── Flow catalog endpoints ──

        group.MapGet("/api/flows", async (HttpContext http, IFlowStore store) =>
        {
            var flows = await store.GetAllAsync();
            await WriteJsonAsync(http.Response,flows);
        });

        group.MapGet("/api/flows/{id:guid}", async (HttpContext http, IFlowStore store, Guid id) =>
        {
            var flow = await store.GetByIdAsync(id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await WriteJsonAsync(http.Response,flow);
        });

        group.MapGet("/api/flows/{id:guid}/mermaid", async (HttpContext http, IFlowRepository repo, Guid id) =>
        {
            var flow = (await repo.GetAllFlowsAsync()).FirstOrDefault(f => f.Id == id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync($"Flow '{id}' not found.");
                return;
            }

            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(flow.ToMermaid());
        });

        group.MapPost("/api/flows/{id:guid}/enable", async (HttpContext http, IFlowStore store, IRecurringTriggerSync triggerSync, Guid id) =>
        {
            try
            {
                var flow = await store.SetEnabledAsync(id, true);
                triggerSync.SyncTriggers(id, true);
                await WriteJsonAsync(http.Response,flow);
            }
            catch (KeyNotFoundException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        group.MapPost("/api/flows/{id:guid}/disable", async (HttpContext http, IFlowStore store, IRecurringTriggerSync triggerSync, Guid id) =>
        {
            try
            {
                var flow = await store.SetEnabledAsync(id, false);
                triggerSync.SyncTriggers(id, false);
                await WriteJsonAsync(http.Response,flow);
            }
            catch (KeyNotFoundException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        group.MapPost("/api/flows/{id:guid}/trigger", async (HttpContext http, IFlowRepository repo, Guid id, IFlowOrchestrator engine) =>
        {
            var flows = await repo.GetAllFlowsAsync();
            var flow = flows.FirstOrDefault(f => f.Id == id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Flow not found in repository." });
                return;
            }

            var (isValidBody, body) = await TryReadJsonBodyAsync(http.Request);
            if (!isValidBody)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Invalid JSON payload." });
                return;
            }

            var headers = (IReadOnlyDictionary<string, string>)http.Request.Headers
                .Where(h => !_sensitiveHeaders.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            var ctx = new TriggerContext
            {
                RunId = Guid.NewGuid(),
                Flow = flow,
                Trigger = new Trigger("manual", "Manual", body, headers)
            };
            await engine.TriggerAsync(ctx);
            await WriteJsonAsync(http.Response,new { runId = ctx.RunId, message = $"Flow '{flow.GetType().Name}' triggered." });
        });

        // Webhook endpoint: POST /flows/api/webhook/{idOrSlug}
        // - idOrSlug = flow GUID: use first Webhook trigger (only Webhook has external URL)
        // - idOrSlug = slug: lookup flow by webhookSlug in Webhook trigger Inputs
        // - If trigger has webhookSecret in Inputs, require X-Webhook-Key header to match
        endpoints.MapPost($"{basePath}/api/webhook/{{idOrSlug}}", async (HttpContext http, IFlowRepository repo, IFlowStore store, string idOrSlug, IFlowOrchestrator engine, [Microsoft.AspNetCore.Mvc.FromServices] FlowOrchestratorTelemetry? telemetry) =>
        {
            // Stitch the run onto the caller's distributed trace by reading the inbound W3C
            // headers and starting our entry-point activity as a child of them. This works
            // even when the host app has not enabled OpenTelemetry's AspNetCore instrumentation.
            // telemetry is nullable so dashboards wired without observability still resolve.
            using var ingressActivity = telemetry is null ? null : InboundTraceContext.StartActivity(
                telemetry.ActivitySource,
                "flow.webhook.receive",
                ActivityKind.Server,
                http.Request.Headers.TryGetValue(InboundTraceContext.TraceparentHeaderName, out var tp) ? tp.FirstOrDefault() : null,
                http.Request.Headers.TryGetValue(InboundTraceContext.TracestateHeaderName, out var ts) ? ts.FirstOrDefault() : null);
            ingressActivity?.SetTag("flow.webhook.slug_or_id", idOrSlug);
            var flows = (await repo.GetAllFlowsAsync()).ToList();
            Core.Abstractions.IFlowDefinition? flow = null;
            string? triggerKey = null;
            string? expectedSecret = null;

            if (Guid.TryParse(idOrSlug, out var flowId))
            {
                flow = flows.FirstOrDefault(f => f.Id == flowId);
                if (flow is not null)
                {
                    var webhookTrigger = flow.Manifest.Triggers.FirstOrDefault(t =>
                        t.Value.Type == TriggerType.Webhook);
                    if (webhookTrigger.Key is not null)
                    {
                        triggerKey = webhookTrigger.Key;
                        if (webhookTrigger.Value?.Inputs.TryGetValue("webhookSecret", out var secretObj) == true && secretObj is string s)
                            expectedSecret = s;
                    }
                }
            }
            else
            {
                foreach (var f in flows)
                {
                    foreach (var (key, meta) in f.Manifest.Triggers)
                    {
                        if (meta.Type != TriggerType.Webhook)
                            continue;
                        if (meta.Inputs.TryGetValue("webhookSlug", out var slugObj) && slugObj is string slug
                            && string.Equals(slug, idOrSlug, StringComparison.OrdinalIgnoreCase))
                        {
                            flow = f;
                            triggerKey = key;
                            if (meta.Inputs.TryGetValue("webhookSecret", out var secretObj) && secretObj is string sec)
                                expectedSecret = sec;
                            break;
                        }
                    }
                    if (flow is not null) break;
                }
            }

            if (flow is null || triggerKey is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Flow or webhook trigger not found." });
                return;
            }

            var record = await store.GetByIdAsync(flow.Id);
            if (record is not null && !record.IsEnabled)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await WriteJsonAsync(http.Response,new { error = "Flow is disabled." });
                return;
            }

            if (!string.IsNullOrEmpty(expectedSecret))
            {
                var providedKey = http.Request.Headers["X-Webhook-Key"].FirstOrDefault()
                    ?? http.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
                // Constant-time compare — string.Equals short-circuits at the first
                // differing character, leaking the secret's prefix through per-request
                // timing measurements. SecureEquals delegates to
                // CryptographicOperations.FixedTimeEquals over the UTF-8 encoded
                // bytes, the same primitive used for the BasicAuth path.
                if (string.IsNullOrEmpty(providedKey) || !SecureEquals(providedKey, expectedSecret))
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await WriteJsonAsync(http.Response,new { error = "Invalid or missing webhook key. Provide X-Webhook-Key header or Authorization: Bearer <key>." });
                    return;
                }
            }

            var (isValidBody, body) = await TryReadJsonBodyAsync(http.Request);
            if (!isValidBody)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Invalid JSON payload." });
                return;
            }

            var headers = (IReadOnlyDictionary<string, string>)http.Request.Headers
                .Where(h => !_sensitiveHeaders.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            var ctx = new TriggerContext
            {
                RunId = Guid.NewGuid(),
                Flow = flow,
                Trigger = new Trigger(triggerKey, TriggerType.Webhook.ToString(), body, headers)
            };
            await engine.TriggerAsync(ctx);
            await WriteJsonAsync(http.Response,new { runId = ctx.RunId, message = $"Flow '{flow.GetType().Name}' triggered via webhook." });
        });

        group.MapGet("/api/handlers", (HttpContext http, IEnumerable<IStepHandlerMetadata> handlers) =>
        {
            var list = handlers.Select(h => new { type = h.Type }).ToList();
            return WriteJsonAsync(http.Response,list);
        });

        // ── Run monitoring endpoints ──

        group.MapGet("/api/runs", async (HttpContext http, IFlowRunStore store) =>
        {
            var query = http.Request.Query;
            Guid? flowId = query.TryGetValue("flowId", out var flowIdValues) && Guid.TryParse(flowIdValues, out var fid) ? fid : null;
            string? status = query.TryGetValue("status", out var statusValues) ? statusValues.ToString() : null;
            string? search = query.TryGetValue("search", out var searchValues) ? searchValues.ToString() : null;
            int skip = query.TryGetValue("skip", out var skipValues) && int.TryParse(skipValues, out var s) ? s : 0;
            int take = query.TryGetValue("take", out var takeValues) && int.TryParse(takeValues, out var t) ? t : 50;
            bool includeTotal = query.TryGetValue("includeTotal", out var includeTotalValues)
                && bool.TryParse(includeTotalValues, out var includeTotalParsed)
                && includeTotalParsed;

            if (includeTotal || !string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(search))
            {
                var page = await store.GetRunsPageAsync(flowId, status, skip, take, search);
                if (includeTotal)
                {
                    await WriteJsonAsync(http.Response,new
                    {
                        items = page.Runs,
                        total = page.TotalCount,
                        skip,
                        take
                    });
                    return;
                }

                await WriteJsonAsync(http.Response,page.Runs);
                return;
            }

            var runs = await store.GetRunsAsync(flowId, skip, take);
            await WriteJsonAsync(http.Response,runs);
        });

        group.MapGet("/api/runs/active", async (HttpContext http, IFlowRunStore store) =>
        {
            var runs = await store.GetActiveRunsAsync();
            await WriteJsonAsync(http.Response,runs);
        });

        group.MapGet("/api/runs/stats", async (HttpContext http, IFlowRunStore store) =>
        {
            var stats = await store.GetStatisticsAsync();
            await WriteJsonAsync(http.Response,stats);
        });

        group.MapGet("/api/runs/{id:guid}", async (HttpContext http, IFlowRunStore store, IOutputsRepository outputsRepository, Guid id) =>
        {
            var run = await store.GetRunDetailAsync(id);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            run.TriggerHeaders = await outputsRepository.GetTriggerHeadersAsync(id);
            await WriteJsonAsync(http.Response,run);
        });

        group.MapGet("/api/runs/{id:guid}/steps", async (HttpContext http, IFlowRunStore store, Guid id) =>
        {
            var run = await store.GetRunDetailAsync(id);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await WriteJsonAsync(http.Response,run.Steps ?? []);
        });

        group.MapGet("/api/runs/{runId:guid}/events", async (HttpContext http, IServiceProvider services, Guid runId) =>
        {
            var reader = services.GetService<IFlowEventReader>();
            if (reader is null)
            {
                await WriteJsonAsync(http.Response,Array.Empty<FlowEventRecord>());
                return;
            }

            var query = http.Request.Query;
            var skip = query.TryGetValue("skip", out var skipValues) && int.TryParse(skipValues, out var parsedSkip)
                ? parsedSkip
                : 0;
            var take = query.TryGetValue("take", out var takeValues) && int.TryParse(takeValues, out var parsedTake)
                ? parsedTake
                : 200;

            var events = await reader.GetRunEventsAsync(runId, skip, take);
            await WriteJsonAsync(http.Response,events);
        });

        group.MapGet("/api/runs/{runId:guid}/control", async (HttpContext http, IServiceProvider services, Guid runId) =>
        {
            var controlStore = services.GetService<IFlowRunControlStore>();
            if (controlStore is null)
            {
                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await WriteJsonAsync(http.Response,new { error = "Run control store is not configured." });
                return;
            }

            var state = await controlStore.GetRunControlAsync(runId);
            if (state is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Run control state not found." });
                return;
            }

            await WriteJsonAsync(http.Response,state);
        });

        group.MapPost("/api/runs/{runId:guid}/cancel", async (HttpContext http, IFlowRunStore store, IServiceProvider services, Guid runId) =>
        {
            var run = await store.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Run not found." });
                return;
            }

            var controlStore = services.GetService<IFlowRunControlStore>();
            if (controlStore is null)
            {
                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await WriteJsonAsync(http.Response,new { error = "Run control store is not configured." });
                return;
            }

            string? reason = null;
            if (http.Request.ContentLength is > 0)
            {
                var payload = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("reason", out var reasonProp)
                    && reasonProp.ValueKind == JsonValueKind.String)
                {
                    reason = reasonProp.GetString();
                }
            }

            var accepted = await controlStore.RequestCancelAsync(runId, reason);

            // If the run has no active steps (zombie / stuck before first dispatch / orphaned
            // by host crash), no engine worker will ever check the cancel flag — close it
            // immediately. We do this even when `accepted == false`, because
            // `RequestCancelAsync` returns false in TWO cases:
            //   (a) cancel was already requested previously (legitimate "already cancelled")
            //   (b) no FlowRunControls row exists for this run (old runs created before the
            //       control store was wired, or runs whose row insertion failed)
            // For (b), the cancel flag UPDATE is a no-op but the user's intent is clear.
            // For (a), force-closing a stuck-Running run is also the right thing to do.
            var closedImmediately = false;
            if (run.Status == "Running")
            {
                var runtimeStore = services.GetService<IFlowRunRuntimeStore>();
                var hasActiveSteps = false;
                if (runtimeStore is not null)
                {
                    var statuses = await runtimeStore.GetStepStatusesAsync(runId);
                    hasActiveSteps = statuses.Values.Any(s =>
                        s is StepStatus.Running or StepStatus.Pending);
                }

                if (!hasActiveSteps)
                {
                    await store.CompleteRunAsync(runId, "Cancelled");
                    closedImmediately = true;
                }
            }

            await WriteJsonAsync(http.Response,new
            {
                success = true,
                accepted = accepted || closedImmediately,
                closedImmediately,
                message = closedImmediately
                    ? $"Run '{runId}' cancelled and closed immediately (no active steps)."
                    : accepted
                        ? $"Cancellation requested for run '{runId}'."
                        : $"Run '{runId}' was already cancelled."
            });
        });

        group.MapPost("/api/runs/{runId:guid}/steps/{stepKey}/retry", async (HttpContext http, IFlowRunStore store, Guid runId, string stepKey) =>
        {
            var run = await store.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Run not found." });
                return;
            }

            var step = run.Steps?.FirstOrDefault(s => s.StepKey == stepKey);
            if (step is null || step.Status != "Failed")
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Step not found or is not in Failed state." });
                return;
            }

            var engine = http.RequestServices.GetRequiredService<IFlowOrchestrator>();
            await engine.RetryStepAsync(run.FlowId, runId, stepKey);
            await WriteJsonAsync(http.Response,new { success = true, message = $"Step '{stepKey}' retry enqueued." });
        });

        group.MapPost("/api/runs/{runId:guid}/signals/{signalName}", async (HttpContext http, IFlowRunStore runStore, IFlowSignalDispatcher signals, Guid runId, string signalName, [Microsoft.AspNetCore.Mvc.FromServices] FlowOrchestratorTelemetry? telemetry) =>
        {
            // Same inbound-traceparent extraction as the webhook endpoint — keeps signal delivery
            // on the caller's trace so APMs can show "approval came in here, run continued there".
            using var ingressActivity = telemetry is null ? null : InboundTraceContext.StartActivity(
                telemetry.ActivitySource,
                "flow.signal.deliver",
                ActivityKind.Server,
                http.Request.Headers.TryGetValue(InboundTraceContext.TraceparentHeaderName, out var tp) ? tp.FirstOrDefault() : null,
                http.Request.Headers.TryGetValue(InboundTraceContext.TracestateHeaderName, out var ts) ? ts.FirstOrDefault() : null);
            ingressActivity?.SetTag("flow.run_id", runId.ToString());
            ingressActivity?.SetTag("flow.signal_name", signalName);

            if (string.IsNullOrWhiteSpace(signalName))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response, new { error = "Signal name is required." });
                return;
            }

            string body;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                body = "{}";
            }

            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response, new { error = "Request body must be valid JSON." });
                return;
            }

            var run = await runStore.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response, new { error = "Run not found." });
                return;
            }
            if (!string.Equals(run.Status, "Running", StringComparison.OrdinalIgnoreCase))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response, new { error = $"Run is not active (status: {run.Status})." });
                return;
            }

            var result = await signals.DispatchAsync(runId, signalName, body, http.RequestAborted);
            switch (result.Status)
            {
                case SignalDeliveryStatus.Delivered:
                    await WriteJsonAsync(http.Response, new
                    {
                        delivered = true,
                        stepKey = result.StepKey,
                        deliveredAt = result.DeliveredAt
                    });
                    return;
                case SignalDeliveryStatus.AlreadyDelivered:
                    http.Response.StatusCode = StatusCodes.Status409Conflict;
                    await WriteJsonAsync(http.Response, new { error = "Signal already delivered.", stepKey = result.StepKey });
                    return;
                default:
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    await WriteJsonAsync(http.Response, new { error = $"No waiter registered for signal '{signalName}' on this run." });
                    return;
            }
        });

        group.MapPost("/api/runs/{runId:guid}/rerun", async (HttpContext http, IFlowRunStore runStore, IFlowRepository repo, IFlowOrchestrator engine, Guid runId) =>
        {
            var run = await runStore.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response, new { error = "Run not found." });
                return;
            }

            var flow = (await repo.GetAllFlowsAsync()).FirstOrDefault(f => f.Id == run.FlowId);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response, new { error = "Flow for this run is no longer registered." });
                return;
            }

            object? data = null;
            if (!string.IsNullOrWhiteSpace(run.TriggerDataJson))
            {
                try { data = JsonDocument.Parse(run.TriggerDataJson).RootElement.Clone(); }
                catch { data = null; }
            }

            var triggerKey = string.IsNullOrWhiteSpace(run.TriggerKey) ? "manual" : run.TriggerKey!;
            var ctx = new TriggerContext
            {
                RunId = Guid.NewGuid(),
                Flow = flow,
                Trigger = new Trigger(triggerKey, triggerKey, data, run.TriggerHeaders),
                SourceRunId = runId  // lineage — links the new run back to the one being re-run
            };
            await engine.TriggerAsync(ctx);
            await WriteJsonAsync(http.Response, new { runId = ctx.RunId, sourceRunId = runId, message = $"Run '{runId}' re-triggered as '{ctx.RunId}'." });
        });

        group.MapGet("/api/runs/{runId:guid}/lineage", async (HttpContext http, IFlowRunStore store, Guid runId) =>
        {
            var run = await store.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response, new { error = "Run not found." });
                return;
            }

            // Source — the run this one was re-run from (if any).
            FlowRunRecord? source = null;
            if (run.SourceRunId is { } sourceId)
            {
                source = await store.GetRunDetailAsync(sourceId);
            }

            // Derived — runs that were re-run from THIS run (children).
            var derived = await store.GetDerivedRunsAsync(runId);

            await WriteJsonAsync(http.Response, new
            {
                runId,
                source = source is null ? null : new
                {
                    id = source.Id,
                    status = source.Status,
                    startedAt = source.StartedAt,
                    completedAt = source.CompletedAt,
                    flowName = source.FlowName
                },
                derived = derived.Select(d => new
                {
                    id = d.Id,
                    status = d.Status,
                    startedAt = d.StartedAt,
                    completedAt = d.CompletedAt,
                    flowName = d.FlowName
                })
            });
        });

        // ── Schedule management endpoints ──

        group.MapGet("/api/schedules", async (HttpContext http, IFlowStore store, IFlowScheduleStateStore scheduleStateStore, IRecurringTriggerInspector inspector) =>
        {
            var recurringJobs = await inspector.GetJobsAsync();

            var flows = await store.GetAllAsync();
            var flowLookup = flows.ToDictionary(f => f.Id);
            var states = await scheduleStateStore.GetAllAsync();
            var stateLookup = states.ToDictionary(s => s.JobId, StringComparer.Ordinal);

            var activeJobs = recurringJobs
                .Where(j => j.Id.StartsWith("flow-"))
                .Select(j =>
                {
                    ParseJobId(j.Id, out var flowId, out var triggerKey);
                    flowLookup.TryGetValue(flowId, out var flow);
                    stateLookup.TryGetValue(j.Id, out var state);
                    var cron = string.IsNullOrWhiteSpace(state?.CronOverride) ? j.Cron : state!.CronOverride!;
                    return new
                    {
                        jobId = j.Id,
                        flowId,
                        flowName = flow?.Name ?? "Unknown",
                        triggerKey,
                        cron,
                        nextExecution = j.NextExecution,
                        lastExecution = j.LastExecution,
                        lastJobId = j.LastJobId,
                        lastJobState = j.LastJobState,
                        timeZoneId = j.TimeZoneId,
                        paused = false
                    };
                })
                .ToList();

            var activeJobIds = activeJobs.Select(x => x.jobId).ToHashSet(StringComparer.Ordinal);
            var pausedJobs = states
                .Where(s => s.IsPaused && !activeJobIds.Contains(s.JobId))
                .Select(s => new
            {
                jobId = s.JobId,
                flowId = s.FlowId,
                flowName = s.FlowName,
                triggerKey = s.TriggerKey,
                cron = s.CronOverride ?? "",
                nextExecution = (DateTime?)null,
                lastExecution = (DateTime?)null,
                // Cast to string? so the anonymous type matches activeJobs' nullable shape
                // — keeps Concat happy without relaxing nullability annotations elsewhere.
                lastJobId = (string?)"",
                lastJobState = (string?)"Paused",
                timeZoneId = (string?)"",
                paused = true
            });

            var result = activeJobs.Concat(pausedJobs).ToList();
            await WriteJsonAsync(http.Response,result);
        });

        group.MapPost("/api/schedules/{jobId}/trigger", async (HttpContext http, IRecurringTriggerDispatcher triggerDispatcher, IFlowScheduleStateStore scheduleStateStore, string jobId) =>
        {
            try
            {
                var state = await scheduleStateStore.GetAsync(jobId);
                if (state?.IsPaused == true)
                {
                    await triggerDispatcher.EnqueueTriggerAsync(state.FlowId, state.TriggerKey);
                    await WriteJsonAsync(http.Response,new { success = true, message = $"Job '{jobId}' triggered." });
                    return;
                }
                triggerDispatcher.TriggerOnce(jobId);
                await WriteJsonAsync(http.Response,new { success = true, message = $"Job '{jobId}' triggered." });
            }
            catch (Exception ex)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = ex.Message });
            }
        });

        group.MapPost("/api/schedules/{jobId}/pause", async (HttpContext http, IRecurringTriggerDispatcher triggerDispatcher, IFlowStore store, IFlowScheduleStateStore scheduleStateStore, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Invalid job ID format." });
                return;
            }

            var flow = await store.GetByIdAsync(flowId);
            var flowName = flow?.Name ?? "Unknown";
            var existing = await scheduleStateStore.GetAsync(jobId);
            var cron = existing?.CronOverride
                ?? (flow?.ManifestJson is not null ? ExtractCronExpression(flow.ManifestJson, triggerKey) : null)
                ?? "";

            await scheduleStateStore.SaveAsync(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = flowId,
                FlowName = flowName,
                TriggerKey = triggerKey,
                IsPaused = true,
                CronOverride = string.IsNullOrWhiteSpace(cron) ? null : cron
            });
            triggerDispatcher.Remove(jobId);
            await WriteJsonAsync(http.Response,new { success = true, message = $"Job '{jobId}' paused." });
        });

        group.MapPost("/api/schedules/{jobId}/resume", async (HttpContext http, IRecurringTriggerDispatcher triggerDispatcher, IFlowStore store, IFlowScheduleStateStore scheduleStateStore, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Invalid job ID format." });
                return;
            }

            var flow = await store.GetByIdAsync(flowId);
            if (flow?.ManifestJson is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Flow not found." });
                return;
            }

            var existing = await scheduleStateStore.GetAsync(jobId);
            var cronExpr = existing?.CronOverride ?? ExtractCronExpression(flow.ManifestJson, triggerKey);
            if (cronExpr is null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Cron expression not found for this trigger." });
                return;
            }

            await scheduleStateStore.SaveAsync(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = flowId,
                FlowName = flow.Name,
                TriggerKey = triggerKey,
                IsPaused = false,
                CronOverride = existing?.CronOverride
            });
            triggerDispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, cronExpr);
            await WriteJsonAsync(http.Response,new { success = true, message = $"Job '{jobId}' resumed with cron '{cronExpr}'." });
        });

        group.MapPut("/api/schedules/{jobId}/cron", async (HttpContext http, IRecurringTriggerDispatcher triggerDispatcher, IFlowStore store, IFlowScheduleStateStore scheduleStateStore, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "Invalid job ID format." });
                return;
            }

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
            if (!body.TryGetProperty("cronExpression", out var cronProp) || string.IsNullOrWhiteSpace(cronProp.GetString()))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(http.Response,new { error = "cronExpression is required." });
                return;
            }
            var newCron = cronProp.GetString()!;

            var flow = await store.GetByIdAsync(flowId);
            if (flow?.ManifestJson is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(http.Response,new { error = "Flow not found." });
                return;
            }

            var existing = await scheduleStateStore.GetAsync(jobId);
            var paused = existing?.IsPaused ?? false;
            await scheduleStateStore.SaveAsync(new FlowScheduleState
            {
                JobId = jobId,
                FlowId = flowId,
                FlowName = flow.Name,
                TriggerKey = triggerKey,
                IsPaused = paused,
                CronOverride = newCron
            });

            if (!paused)
            {
                triggerDispatcher.RegisterOrUpdate(jobId, flowId, triggerKey, newCron);
            }

            await WriteJsonAsync(http.Response,new { success = true, message = $"Cron updated to '{newCron}'." });
        });

        return endpoints;
    }

    // We deliberately avoid HttpResponse.WriteAsJsonAsync — that helper writes
    // through HttpResponse.BodyWriter (a PipeWriter), and ASP.NET Core TestHost's
    // ResponseBodyPipeWriter did not implement PipeWriter.UnflushedBytes until
    // .NET 10, breaking the integration test suite under net8/net9.
    //
    // JsonSerializer.SerializeAsync(Stream, ...) sidesteps that problem — it
    // writes through a pooled byte-buffer writer directly to the response Stream,
    // never touching the PipeWriter. This was previously implemented as
    // `WriteAsync(JsonSerializer.Serialize(value))` which round-tripped through
    // an intermediate UTF-16 string of size O(payload). For a typical
    // /api/runs response (~60 KB JSON) that intermediate alloc dominated the
    // request's GC pressure. Switching to SerializeAsync writes directly into
    // the response Stream and reduces per-request allocation by an order of
    // magnitude on the larger endpoints.
    //
    // Wire format is byte-identical: the same JsonSerializerOptions instance
    // is used in both paths.
    private static readonly JsonSerializerOptions _camelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Task WriteJsonAsync<T>(HttpResponse response, T value)
    {
        response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(response.Body, value, _camelCaseOptions);
    }

    /// <summary>
    /// Writes the dashboard root HTML to the response, picking the best
    /// pre-compressed buffer for the client's <c>Accept-Encoding</c> header.
    /// </summary>
    /// <remarks>
    /// Brotli is preferred when advertised; Gzip is the universal fallback;
    /// the raw UTF-8 string is served when neither is supported.
    /// <c>Vary: Accept-Encoding</c> is set on every response so any cache
    /// (CDN, browser, reverse proxy) keys the variant correctly.
    /// </remarks>
    private static Task WriteCompressedHtmlAsync(HttpContext http, PrecompressedDashboardPage page)
    {
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers.Vary = "Accept-Encoding";

        var accept = http.Request.Headers.AcceptEncoding.ToString();

        if (HasEncoding(accept, "br"))
        {
            http.Response.Headers.ContentEncoding = "br";
            return http.Response.Body.WriteAsync(page.BrotliBytes, 0, page.BrotliBytes.Length);
        }

        if (HasEncoding(accept, "gzip"))
        {
            http.Response.Headers.ContentEncoding = "gzip";
            return http.Response.Body.WriteAsync(page.GzipBytes, 0, page.GzipBytes.Length);
        }

        return http.Response.WriteAsync(page.Html);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="acceptEncoding"/> advertises
    /// <paramref name="token"/> as a coding with q-value &gt; 0.
    /// </summary>
    /// <remarks>
    /// Per RFC 7231 §5.3.4, an entry like <c>br;q=0</c> means "do not use Brotli
    /// even if you support it". This parser honors that: an explicit zero
    /// q-value excludes the coding; any other (or omitted) q-value accepts it.
    /// Full q-value precedence between codings (e.g. preferring <c>br;q=0.9</c>
    /// over <c>gzip;q=0.5</c>) is not implemented because real-world browsers
    /// only emit unweighted lists or the q=0 disable form.
    /// </remarks>
    private static bool HasEncoding(string acceptEncoding, string token)
    {
        if (string.IsNullOrEmpty(acceptEncoding))
        {
            return false;
        }

        foreach (var part in acceptEncoding.Split(','))
        {
            var entry = part.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            // Split "<coding>" from "<coding>;<params>".
            var semi = entry.IndexOf(';');
            var coding = (semi < 0 ? entry : entry[..semi]).Trim();
            if (!coding.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (semi < 0)
            {
                return true; // bare coding — implicitly q=1
            }

            // Walk the ";q=<value>" parameter list. Anything that's not a
            // valid double, or any q-value > 0, is treated as accepted.
            foreach (var rawParam in entry[(semi + 1)..].Split(';'))
            {
                var p = rawParam.Trim();
                if (!p.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = p[2..].Trim();
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)
                    && q <= 0d)
                {
                    return false;
                }

                return true;
            }

            return true; // matched coding, no q parameter
        }

        return false;
    }

    private static async ValueTask<(bool IsValidBody, object? Body)> TryReadJsonBodyAsync(HttpRequest request)
    {
        if (request.ContentLength == 0)
        {
            return (true, null);
        }

        try
        {
            if (request.ContentLength is > 0)
            {
                var body = await JsonSerializer.DeserializeAsync<object>(request.Body);
                return (true, body);
            }

            // Content-Length may be null for chunked requests. Buffer once so we can
            // treat empty payload as "no body" instead of throwing JSON parse errors.
            using var buffered = new MemoryStream();
            await request.Body.CopyToAsync(buffered);
            if (buffered.Length == 0)
            {
                return (true, null);
            }

            buffered.Position = 0;
            var chunkedBody = await JsonSerializer.DeserializeAsync<object>(buffered);
            return (true, chunkedBody);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Endpoint filter that enforces HTTP Basic Authentication on all dashboard routes
    /// when <see cref="FlowDashboardBasicAuthOptions.IsEnabled"/> is true.
    /// </summary>
    private sealed class FlowDashboardBasicAuthFilter(FlowDashboardBasicAuthOptions options) : IEndpointFilter
    {
        /// <inheritdoc/>
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (IsAuthorized(context.HttpContext, options))
            {
                return next(context);
            }

            var realm = string.IsNullOrWhiteSpace(options.Realm)
                ? "FlowOrchestrator Dashboard"
                : options.Realm.Replace("\"", string.Empty, StringComparison.Ordinal);

            context.HttpContext.Response.Headers.WWWAuthenticate = $"Basic realm=\"{realm}\", charset=\"UTF-8\"";
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }
    }

    private static bool IsAuthorized(HttpContext http, FlowDashboardBasicAuthOptions options)
    {
        if (!options.IsEnabled)
        {
            return true;
        }

        if (!http.Request.Headers.TryGetValue("Authorization", out var authHeaders))
        {
            return false;
        }

        const string basicPrefix = "Basic ";
        var headerValue = authHeaders.ToString();
        if (!headerValue.StartsWith(basicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = headerValue[basicPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        string userAndPassword;
        try
        {
            var decodedBytes = Convert.FromBase64String(encoded);
            userAndPassword = Encoding.UTF8.GetString(decodedBytes);
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = userAndPassword.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var username = userAndPassword[..separatorIndex];
        var password = userAndPassword[(separatorIndex + 1)..];
        return SecureEquals(username, options.Username!) &&
               SecureEquals(password, options.Password!);
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool ParseJobId(string jobId, out Guid flowId, out string triggerKey)
    {
        flowId = Guid.Empty;
        triggerKey = "";

        if (!jobId.StartsWith("flow-")) return false;

        var withoutPrefix = jobId["flow-".Length..];
        // GUID format: 8-4-4-4-12 = 36 chars, then '-' then triggerKey
        if (withoutPrefix.Length < 38) return false;

        var guidPart = withoutPrefix[..36];
        triggerKey = withoutPrefix[37..];

        return Guid.TryParse(guidPart, out flowId) && !string.IsNullOrEmpty(triggerKey);
    }

    private static string? ExtractCronExpression(string manifestJson, string triggerKey)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(manifestJson);
            if (doc.TryGetProperty("triggers", out var triggers) || doc.TryGetProperty("Triggers", out triggers))
            {
                if (triggers.TryGetProperty(triggerKey, out var trigger))
                {
                    if (trigger.TryGetProperty("inputs", out var inputs) || trigger.TryGetProperty("Inputs", out inputs))
                    {
                        if (inputs.TryGetProperty("cronExpression", out var cron))
                            return cron.GetString();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Fallback in-memory schedule state store registered when no persistent
    /// <see cref="IFlowScheduleStateStore"/> is provided by the storage backend.
    /// State is lost on process restart.
    /// </summary>
    private sealed class InMemoryScheduleStateStore : IFlowScheduleStateStore
    {
        private readonly ConcurrentDictionary<string, FlowScheduleState> _states = new(StringComparer.Ordinal);

        public Task<FlowScheduleState?> GetAsync(string jobId)
        {
            _states.TryGetValue(jobId, out var state);
            return Task.FromResult<FlowScheduleState?>(state);
        }

        public Task<IReadOnlyList<FlowScheduleState>> GetAllAsync()
        {
            IReadOnlyList<FlowScheduleState> states = _states.Values
                .OrderBy(x => x.JobId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(states);
        }

        public Task SaveAsync(FlowScheduleState state)
        {
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _states[state.JobId] = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string jobId)
        {
            _states.TryRemove(jobId, out _);
            return Task.CompletedTask;
        }
    }

}
