using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.Hangfire;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlowOrchestrator.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    // Headers excluded from trigger capture: sensitive auth/session headers and low-level transport headers
    private static readonly HashSet<string> _sensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie",
        "X-Webhook-Key", "Proxy-Authorization",
        "Connection", "Transfer-Encoding", "Upgrade", "Content-Length"
    };
    public static IServiceCollection AddFlowDashboard(this IServiceCollection services)
    {
        services.AddOptions<FlowDashboardOptions>();
        return services;
    }

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

    public static IServiceCollection AddFlowDashboard(
        this IServiceCollection services,
        Action<FlowDashboardOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddFlowDashboard();
        services.Configure(configureOptions);
        return services;
    }

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

        var html = DashboardHtml.Render(basePath, options.Branding);

        group.MapGet("", (HttpContext http) =>
        {
            http.Response.ContentType = "text/html; charset=utf-8";
            return http.Response.WriteAsync(html);
        });

        // ── Flow catalog endpoints ──

        group.MapGet("/api/flows", async (HttpContext http, IFlowStore store) =>
        {
            var flows = await store.GetAllAsync();
            await http.Response.WriteAsJsonAsync(flows);
        });

        group.MapGet("/api/flows/{id:guid}", async (HttpContext http, IFlowStore store, Guid id) =>
        {
            var flow = await store.GetByIdAsync(id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(flow);
        });

        group.MapPost("/api/flows/{id:guid}/enable", async (HttpContext http, IFlowStore store, IRecurringTriggerSync triggerSync, Guid id) =>
        {
            try
            {
                var flow = await store.SetEnabledAsync(id, true);
                triggerSync.SyncTriggers(id, true);
                await http.Response.WriteAsJsonAsync(flow);
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
                await http.Response.WriteAsJsonAsync(flow);
            }
            catch (KeyNotFoundException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        group.MapPost("/api/flows/{id:guid}/trigger", async (HttpContext http, IFlowRepository repo, Guid id, IHangfireFlowTrigger flowTrigger) =>
        {
            var flows = await repo.GetAllFlowsAsync();
            var flow = flows.FirstOrDefault(f => f.Id == id);
            if (flow is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Flow not found in repository." });
                return;
            }

            var (isValidBody, body) = await TryReadJsonBodyAsync(http.Request);
            if (!isValidBody)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload." });
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
            await flowTrigger.TriggerAsync(ctx);
            await http.Response.WriteAsJsonAsync(new { runId = ctx.RunId, message = $"Flow '{flow.GetType().Name}' triggered." });
        });

        // Webhook endpoint: POST /flows/api/webhook/{idOrSlug}
        // - idOrSlug = flow GUID: use first Webhook trigger (only Webhook has external URL)
        // - idOrSlug = slug: lookup flow by webhookSlug in Webhook trigger Inputs
        // - If trigger has webhookSecret in Inputs, require X-Webhook-Key header to match
        endpoints.MapPost($"{basePath}/api/webhook/{{idOrSlug}}", async (HttpContext http, IFlowRepository repo, IFlowStore store, string idOrSlug, IHangfireFlowTrigger flowTrigger) =>
        {
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
                await http.Response.WriteAsJsonAsync(new { error = "Flow or webhook trigger not found." });
                return;
            }

            var record = await store.GetByIdAsync(flow.Id);
            if (record is not null && !record.IsEnabled)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(new { error = "Flow is disabled." });
                return;
            }

            if (!string.IsNullOrEmpty(expectedSecret))
            {
                var providedKey = http.Request.Headers["X-Webhook-Key"].FirstOrDefault()
                    ?? http.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!string.Equals(providedKey, expectedSecret, StringComparison.Ordinal))
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await http.Response.WriteAsJsonAsync(new { error = "Invalid or missing webhook key. Provide X-Webhook-Key header or Authorization: Bearer <key>." });
                    return;
                }
            }

            var (isValidBody, body) = await TryReadJsonBodyAsync(http.Request);
            if (!isValidBody)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload." });
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
            await flowTrigger.TriggerAsync(ctx);
            await http.Response.WriteAsJsonAsync(new { runId = ctx.RunId, message = $"Flow '{flow.GetType().Name}' triggered via webhook." });
        });

        group.MapGet("/api/handlers", (HttpContext http, IEnumerable<IStepHandlerMetadata> handlers) =>
        {
            var list = handlers.Select(h => new { type = h.Type }).ToList();
            return http.Response.WriteAsJsonAsync(list);
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
                    await http.Response.WriteAsJsonAsync(new
                    {
                        items = page.Runs,
                        total = page.TotalCount,
                        skip,
                        take
                    });
                    return;
                }

                await http.Response.WriteAsJsonAsync(page.Runs);
                return;
            }

            var runs = await store.GetRunsAsync(flowId, skip, take);
            await http.Response.WriteAsJsonAsync(runs);
        });

        group.MapGet("/api/runs/active", async (HttpContext http, IFlowRunStore store) =>
        {
            var runs = await store.GetActiveRunsAsync();
            await http.Response.WriteAsJsonAsync(runs);
        });

        group.MapGet("/api/runs/stats", async (HttpContext http, IFlowRunStore store) =>
        {
            var stats = await store.GetStatisticsAsync();
            await http.Response.WriteAsJsonAsync(stats);
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
            await http.Response.WriteAsJsonAsync(run);
        });

        group.MapGet("/api/runs/{id:guid}/steps", async (HttpContext http, IFlowRunStore store, Guid id) =>
        {
            var run = await store.GetRunDetailAsync(id);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await http.Response.WriteAsJsonAsync(run.Steps ?? []);
        });

        group.MapPost("/api/runs/{runId:guid}/steps/{stepKey}/retry", async (HttpContext http, IFlowRunStore store, Guid runId, string stepKey) =>
        {
            var run = await store.GetRunDetailAsync(runId);
            if (run is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Run not found." });
                return;
            }

            var step = run.Steps?.FirstOrDefault(s => s.StepKey == stepKey);
            if (step is null || step.Status != "Failed")
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Step not found or is not in Failed state." });
                return;
            }

            BackgroundJob.Enqueue<IHangfireStepRunner>(r => r.RetryStepAsync(run.FlowId, runId, stepKey, null));
            await http.Response.WriteAsJsonAsync(new { success = true, message = $"Step '{stepKey}' retry enqueued." });
        });

        // ── Schedule management endpoints ──

        group.MapGet("/api/schedules", async (HttpContext http, IFlowStore store) =>
        {
            using var connection = JobStorage.Current.GetConnection();
            var recurringJobs = connection.GetRecurringJobs();

            var flows = await store.GetAllAsync();
            var flowLookup = flows.ToDictionary(f => f.Id);

            var activeJobs = recurringJobs
                .Where(j => j.Id.StartsWith("flow-"))
                .Select(j =>
                {
                    ParseJobId(j.Id, out var flowId, out var triggerKey);
                    flowLookup.TryGetValue(flowId, out var flow);
                    return new
                    {
                        jobId = j.Id,
                        flowId,
                        flowName = flow?.Name ?? "Unknown",
                        triggerKey,
                        cron = j.Cron,
                        nextExecution = j.NextExecution,
                        lastExecution = j.LastExecution,
                        lastJobId = j.LastJobId,
                        lastJobState = j.LastJobState,
                        timeZoneId = j.TimeZoneId,
                        paused = false
                    };
                })
                .ToList();

            var pausedJobs = PausedJobsStore.GetAll().Select(p => new
            {
                jobId = p.JobId,
                flowId = p.FlowId,
                flowName = p.FlowName,
                triggerKey = p.TriggerKey,
                cron = p.Cron,
                nextExecution = (DateTime?)null,
                lastExecution = (DateTime?)null,
                lastJobId = "",
                lastJobState = "Paused",
                timeZoneId = "",
                paused = true
            });

            var result = activeJobs.Concat(pausedJobs).ToList();
            await http.Response.WriteAsJsonAsync(result);
        });

        group.MapPost("/api/schedules/{jobId}/trigger", async (HttpContext http, IRecurringJobManager manager, string jobId) =>
        {
            try
            {
                if (PausedJobsStore.TryGet(jobId, out var paused))
                {
                    BackgroundJob.Enqueue<IHangfireFlowTrigger>(t => t.TriggerByScheduleAsync(paused.FlowId, paused.TriggerKey, null));
                    await http.Response.WriteAsJsonAsync(new { success = true, message = $"Job '{jobId}' triggered." });
                    return;
                }
                manager.Trigger(jobId);
                await http.Response.WriteAsJsonAsync(new { success = true, message = $"Job '{jobId}' triggered." });
            }
            catch (Exception ex)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });

        group.MapPost("/api/schedules/{jobId}/pause", async (HttpContext http, IRecurringJobManager manager, IFlowStore store, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Invalid job ID format." });
                return;
            }

            var flow = await store.GetByIdAsync(flowId);
            var flowName = flow?.Name ?? "Unknown";
            var cron = flow?.ManifestJson is not null ? ExtractCronExpression(flow.ManifestJson, triggerKey) ?? "" : "";

            PausedJobsStore.Add(jobId, flowId, flowName, triggerKey, cron);
            manager.RemoveIfExists(jobId);
            await http.Response.WriteAsJsonAsync(new { success = true, message = $"Job '{jobId}' paused." });
        });

        group.MapPost("/api/schedules/{jobId}/resume", async (HttpContext http, IRecurringJobManager manager, IFlowStore store, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Invalid job ID format." });
                return;
            }

            var flow = await store.GetByIdAsync(flowId);
            if (flow?.ManifestJson is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Flow not found." });
                return;
            }

            var cronExpr = ExtractCronExpression(flow.ManifestJson, triggerKey);
            if (cronExpr is null)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Cron expression not found for this trigger." });
                return;
            }

            PausedJobsStore.Remove(jobId);
            var fid = flowId;
            var key = triggerKey;
            manager.AddOrUpdate<IHangfireFlowTrigger>(jobId, t => t.TriggerByScheduleAsync(fid, key, null), cronExpr);
            await http.Response.WriteAsJsonAsync(new { success = true, message = $"Job '{jobId}' resumed with cron '{cronExpr}'." });
        });

        group.MapPut("/api/schedules/{jobId}/cron", async (HttpContext http, IRecurringJobManager manager, IFlowStore store, string jobId) =>
        {
            if (!ParseJobId(jobId, out var flowId, out var triggerKey))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "Invalid job ID format." });
                return;
            }

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
            if (!body.TryGetProperty("cronExpression", out var cronProp) || string.IsNullOrWhiteSpace(cronProp.GetString()))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { error = "cronExpression is required." });
                return;
            }
            var newCron = cronProp.GetString()!;

            var flow = await store.GetByIdAsync(flowId);
            if (flow?.ManifestJson is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "Flow not found." });
                return;
            }

            var manifestDoc = JsonSerializer.Deserialize<JsonElement>(flow.ManifestJson);
            var updatedManifest = UpdateCronInManifest(manifestDoc, triggerKey, newCron);
            flow.ManifestJson = JsonSerializer.Serialize(updatedManifest);
            flow.UpdatedAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(flow);

            var fid = flowId;
            var key = triggerKey;
            manager.AddOrUpdate<IHangfireFlowTrigger>(jobId, t => t.TriggerByScheduleAsync(fid, key, null), newCron);

            await http.Response.WriteAsJsonAsync(new { success = true, message = $"Cron updated to '{newCron}'." });
        });

        return endpoints;
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

    private sealed class FlowDashboardBasicAuthFilter(FlowDashboardBasicAuthOptions options) : IEndpointFilter
    {
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

    private static JsonElement UpdateCronInManifest(JsonElement manifest, string triggerKey, string newCron)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(manifest.GetRawText())
            ?? new Dictionary<string, JsonElement>();

        var triggersKey = dict.ContainsKey("triggers") ? "triggers" : "Triggers";
        if (!dict.ContainsKey(triggersKey)) return manifest;

        var triggers = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dict[triggersKey].GetRawText())
            ?? new Dictionary<string, JsonElement>();

        if (!triggers.ContainsKey(triggerKey)) return manifest;

        var trigger = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(triggers[triggerKey].GetRawText())
            ?? new Dictionary<string, JsonElement>();

        var inputsKey = trigger.ContainsKey("inputs") ? "inputs" : "Inputs";
        var inputs = trigger.ContainsKey(inputsKey)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(trigger[inputsKey].GetRawText()) ?? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>();

        inputs["cronExpression"] = JsonSerializer.Deserialize<JsonElement>($"\"{newCron}\"");
        trigger[inputsKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(inputs));
        triggers[triggerKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(trigger));
        dict[triggersKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(triggers));

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dict));
    }

    private static class PausedJobsStore
    {
        private static readonly ConcurrentDictionary<string, PausedJobInfo> _store = new();

        public static void Add(string jobId, Guid flowId, string flowName, string triggerKey, string cron)
        {
            _store[jobId] = new PausedJobInfo(jobId, flowId, flowName, triggerKey, cron);
        }

        public static void Remove(string jobId) => _store.TryRemove(jobId, out _);

        public static bool TryGet(string jobId, out PausedJobInfo info) => _store.TryGetValue(jobId, out info!);

        public static IEnumerable<PausedJobInfo> GetAll() => _store.Values;

        public record PausedJobInfo(string JobId, Guid FlowId, string FlowName, string TriggerKey, string Cron);
    }
}
