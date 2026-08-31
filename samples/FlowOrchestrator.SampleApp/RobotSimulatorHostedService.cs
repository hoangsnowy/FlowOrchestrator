using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FlowOrchestrator.Core.Execution;
using FlowOrchestrator.Core.Storage;
using FlowOrchestrator.SampleApp.Flows;

namespace FlowOrchestrator.SampleApp;

/// <summary>
/// Plays the warehouse robot for <see cref="WarehouseRobotFlow"/>: watches active runs for a
/// parked <c>wait_robot_goto</c> iteration, "drives" for a few seconds, then delivers the
/// <c>robot_goto</c> signal with the location it reached — so a single trigger plays the whole
/// scan job out on the dashboard with no manual signalling.
/// </summary>
/// <remarks>
/// <para>
/// The simulator reads only public storage abstractions (<see cref="IFlowRunStore"/>,
/// <see cref="IOutputsRepository"/>) and signals through <see cref="IFlowSignalDispatcher"/> —
/// exactly what a real robot-controller integration would call, which is what makes the sample
/// behave like production: the flow itself has no idea a simulator exists.
/// </para>
/// <para>
/// One signal is delivered per run per sweep, in iteration order, mirroring a single robot
/// visiting locations sequentially (the flow's <c>ConcurrencyLimit = 1</c>). The dispatcher
/// routes a shared signal name to the earliest undelivered waiter, which registers in
/// iteration order under that concurrency limit — the delivery log records the actual
/// runtime step key the signal landed on.
/// </para>
/// <para>
/// Disable with <c>ROBOT_SIMULATOR=false</c> to drive the robot yourself:
/// <c>POST /flows/api/runs/{runId}/signals/robot_goto</c> with <c>{"Location":"A-01-03"}</c>.
/// </para>
/// </remarks>
public sealed class RobotSimulatorHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RobotSimulatorHostedService> _logger;

    // (runId, runtimeStepKey) → when the "robot" arrives. Entries for finished runs are pruned
    // each sweep; the whole map is tiny (one entry per parked iteration).
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), DateTimeOffset> _travelling = new();
    private readonly ConcurrentDictionary<(Guid RunId, string StepKey), bool> _delivered = new();

    /// <summary>Initialises the simulator with the root service provider for per-sweep scopes.</summary>
    public RobotSimulatorHostedService(IServiceProvider serviceProvider, ILogger<RobotSimulatorHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RobotSimulator] online — watching WarehouseRobotFlow runs (set ROBOT_SIMULATOR=false to drive the robot manually).");

        using var timer = new PeriodicTimer(SweepInterval);
        while (await WaitForNextSweepAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The simulator must never take the sample host down — log and try again next second.
                _logger.LogWarning(ex, "[RobotSimulator] sweep failed — retrying next interval.");
            }
        }
    }

    private static async Task<bool> WaitForNextSweepAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IFlowRunStore>();
        var outputs = scope.ServiceProvider.GetRequiredService<IOutputsRepository>();
        var signals = scope.ServiceProvider.GetRequiredService<IFlowSignalDispatcher>();
        var flowId = new WarehouseRobotFlow().Id;

        var activeRuns = await runStore.GetActiveRunsAsync().ConfigureAwait(false);
        var activeIds = new HashSet<Guid>();

        foreach (var run in activeRuns)
        {
            if (run.FlowId != flowId)
            {
                continue;
            }

            activeIds.Add(run.Id);
            var detail = await runStore.GetRunDetailAsync(run.Id).ConfigureAwait(false);
            if (detail?.Steps is null)
            {
                continue;
            }

            // Parked waiters, in iteration order — the robot visits one location at a time.
            var parked = detail.Steps
                .Where(step => step.Status == "Pending"
                               && step.StepKey.StartsWith("scan_process.", StringComparison.Ordinal)
                               && step.StepKey.EndsWith(".wait_robot_goto", StringComparison.Ordinal)
                               && !_delivered.ContainsKey((run.Id, step.StepKey)))
                .OrderBy(step => step.StepKey, StringComparer.Ordinal)
                .ToList();

            if (parked.Count == 0)
            {
                continue;
            }

            var next = parked[0];
            var key = (run.Id, next.StepKey);

            // First sighting: start "driving" — arrive 2–4.5 s later.
            if (!_travelling.TryGetValue(key, out var arriveAt))
            {
                arriveAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(2_000, 4_500));
                _travelling[key] = arriveAt;
                _logger.LogInformation(
                    "[RobotSimulator] RunId={RunId} robot driving to the location for {StepKey} (ETA {EtaSeconds:F1}s)",
                    run.Id, next.StepKey, (arriveAt - DateTimeOffset.UtcNow).TotalSeconds);
                continue;
            }

            if (DateTimeOffset.UtcNow < arriveAt)
            {
                continue;
            }

            var location = await ResolveLocationAsync(outputs, run.Id, next.StepKey).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new { Location = location, ReachedAt = DateTimeOffset.UtcNow });
            var result = await signals.DispatchAsync(run.Id, "robot_goto", payload, ct).ConfigureAwait(false);

            _delivered[key] = true;
            _travelling.TryRemove(key, out _);
            _logger.LogInformation(
                "[RobotSimulator] RunId={RunId} robot arrived at {Location} — signal delivered to {StepKey} ({Status})",
                run.Id, location, result.StepKey ?? next.StepKey, result.Status);
        }

        PruneFinishedRuns(activeIds);
    }

    /// <summary>
    /// Reads the trigger body's <c>Locations</c> array and picks the entry matching the parked
    /// iteration's index, falling back to a synthetic aisle code when absent.
    /// </summary>
    private static async Task<string> ResolveLocationAsync(IOutputsRepository outputs, Guid runId, string stepKey)
    {
        // "scan_process.{index}.wait_robot_goto" → {index}
        var segments = stepKey.Split('.');
        var index = segments.Length >= 2 && int.TryParse(segments[1], out var parsed) ? parsed : 0;

        try
        {
            var trigger = await outputs.GetTriggerDataAsync(runId).ConfigureAwait(false);
            if (trigger is JsonElement { ValueKind: JsonValueKind.Object } body
                && body.TryGetProperty("Locations", out var locations)
                && locations.ValueKind == JsonValueKind.Array
                && index < locations.GetArrayLength())
            {
                var value = locations[index];
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }
        catch (Exception)
        {
            // Trigger data unavailable or oddly shaped — fall through to the synthetic code.
        }

        return $"AISLE-{index + 1:00}";
    }

    private void PruneFinishedRuns(HashSet<Guid> activeIds)
    {
        foreach (var key in _travelling.Keys)
        {
            if (!activeIds.Contains(key.RunId))
            {
                _travelling.TryRemove(key, out _);
            }
        }

        foreach (var key in _delivered.Keys)
        {
            if (!activeIds.Contains(key.RunId))
            {
                _delivered.TryRemove(key, out _);
            }
        }
    }
}
