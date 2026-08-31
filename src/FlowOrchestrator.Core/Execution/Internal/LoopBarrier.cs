using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Completion barrier for scoped (loop / ForEach) steps: keeps a loop step in
/// <see cref="StepStatus.Running"/> from the moment it fans out until every iteration of
/// its body has reached a terminal status, then settles it as <see cref="StepStatus.Succeeded"/>.
/// </summary>
/// <remarks>
/// <para>
/// Without the barrier the loop step reported <see cref="StepStatus.Succeeded"/> the instant
/// it enqueued its children, so a step declaring <c>RunAfter = { loop: [Succeeded] }</c> was
/// dispatched in parallel with — not after — the iterations. With fast children the race was
/// invisible; with a parked child (<c>WaitForSignal</c>, a polling step) the downstream step
/// ran first, which is issue #169.
/// </para>
/// <para>
/// The iteration count is read back from the loop step's own persisted output
/// (<c>{"iterations":N}</c>), which the engine writes <b>before</b> it dispatches any child.
/// Counting dispatched or started children instead would be racy: iteration 0 can finish on
/// another worker while the fan-out loop is still enqueuing iteration 1.
/// </para>
/// <para>
/// Settling is idempotent rather than exclusive: two workers completing the last two children
/// concurrently can both observe "all terminal" and both write the same
/// <see cref="StepStatus.Succeeded"/> row. The write is an UPDATE, and the downstream dispatch
/// it unblocks is guarded by the dispatch ledger, so the duplicate is harmless. A ledger-based
/// latch was rejected because a synthetic key never gets a status row and would make
/// <c>HasInFlightWorkAsync</c> consider the run permanently in flight.
/// </para>
/// </remarks>
internal static class LoopBarrier
{
    private static readonly JsonSerializerOptions _webOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Property name carrying the iteration count in a loop step's output.</summary>
    private const string IterationCountProperty = "iterations";

    /// <summary>
    /// Returns the runtime keys of the loop steps enclosing <paramref name="runtimeStepKey"/>,
    /// innermost first — e.g. <c>"outer.1.inner.0.child"</c> yields
    /// <c>["outer.1.inner", "outer"]</c>. Empty for a top-level step.
    /// </summary>
    /// <param name="runtimeStepKey">A runtime step key, possibly carrying iteration indices.</param>
    public static IReadOnlyList<string> EnclosingLoopKeys(string runtimeStepKey)
    {
        if (string.IsNullOrEmpty(runtimeStepKey) || !runtimeStepKey.Contains('.', StringComparison.Ordinal))
        {
            return [];
        }

        var segments = runtimeStepKey.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var loopKeys = new List<string>();

        // A numeric segment is an iteration index; everything before it is the loop's runtime key.
        for (var i = segments.Length - 1; i >= 1; i--)
        {
            if (int.TryParse(segments[i], out _))
            {
                loopKeys.Add(string.Join('.', segments, 0, i));
            }
        }

        return loopKeys;
    }

    /// <summary>
    /// Reads the iteration count a loop step recorded in its output.
    /// </summary>
    /// <param name="loopOutput">
    /// The stored output, as returned by <see cref="IOutputsRepository.GetStepOutputAsync"/> —
    /// a <see cref="JsonElement"/> for every first-party store, but tolerant of a raw
    /// dictionary or boxed integer so custom stores keep working.
    /// </param>
    /// <param name="iterations">The number of iterations the loop fanned out.</param>
    /// <returns><see langword="true"/> when a non-negative count could be read.</returns>
    public static bool TryReadIterationCount(object? loopOutput, out int iterations)
    {
        iterations = 0;
        switch (loopOutput)
        {
            case null:
                return false;

            case int boxed:
                iterations = boxed;
                return boxed >= 0;

            case JsonElement element:
                return TryReadFromJson(element, out iterations);

            case IDictionary<string, object?> dictionary:
                foreach (var entry in dictionary)
                {
                    if (!string.Equals(entry.Key, IterationCountProperty, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return TryReadIterationCount(entry.Value, out iterations);
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Determines whether every step of every iteration of <paramref name="runtimeLoopKey"/>
    /// has reached a terminal status.
    /// </summary>
    /// <param name="flow">Flow whose manifest supplies the loop body's child keys.</param>
    /// <param name="runtimeLoopKey">Runtime key of the loop step (may itself be scoped).</param>
    /// <param name="iterations">Iteration count read from the loop's output.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    /// <remarks>
    /// A child that never ran because its <c>RunAfter</c> could not be satisfied is recorded
    /// <see cref="StepStatus.Skipped"/> by the engine's blocked-step pass, which is terminal —
    /// so a failing iteration settles the barrier instead of deadlocking it. A nested loop child
    /// settles through its own barrier before it counts as terminal here.
    /// </remarks>
    public static bool AllIterationsSettled(
        IFlowDefinition flow,
        string runtimeLoopKey,
        int iterations,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        if (flow.Manifest.Steps.FindStep(runtimeLoopKey) is not IScopedStep scoped || scoped.Steps.Count == 0)
        {
            return true;
        }

        for (var index = 0; index < iterations; index++)
        {
            var iterationPrefix = $"{runtimeLoopKey}.{index}.";
            if (scoped.Steps.Keys.Any(childKey =>
                    !statuses.TryGetValue($"{iterationPrefix}{childKey}", out var status)
                    || !IsTerminal(status)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Settles every candidate loop step whose iterations have all reached a terminal status,
    /// persisting <see cref="StepStatus.Succeeded"/> for it.
    /// </summary>
    /// <param name="flow">The flow being executed.</param>
    /// <param name="runId">The run whose loop steps are being settled.</param>
    /// <param name="candidateLoopKeys">
    /// Runtime loop keys to consider, innermost first — settling an inner loop can settle the
    /// outer one in the same pass, so ordering matters.
    /// </param>
    /// <param name="statuses">Status map as read before this pass; not mutated.</param>
    /// <param name="outputs">Repository the iteration count is read from.</param>
    /// <param name="runStore">Store the settled status is written to.</param>
    /// <returns>The loop keys that were settled by this call, in the order they settled.</returns>
    public static async Task<IReadOnlyList<string>> SettleAsync(
        IFlowDefinition flow,
        Guid runId,
        IReadOnlyList<string> candidateLoopKeys,
        IReadOnlyDictionary<string, StepStatus> statuses,
        IOutputsRepository outputs,
        IFlowRunStore runStore)
    {
        if (candidateLoopKeys.Count == 0)
        {
            return [];
        }

        // Local copy so an inner loop settled in this pass is visible when the outer loop's
        // children are checked, without a storage round-trip per level.
        Dictionary<string, StepStatus>? working = null;
        List<string>? settled = null;

        foreach (var loopKey in candidateLoopKeys)
        {
            var view = (IReadOnlyDictionary<string, StepStatus>?)working ?? statuses;
            if (!view.TryGetValue(loopKey, out var loopStatus) || loopStatus != StepStatus.Running)
            {
                continue;
            }

            if (flow.Manifest.Steps.FindStep(loopKey) is not IScopedStep)
            {
                continue;
            }

            var output = await outputs.GetStepOutputAsync(runId, loopKey).ConfigureAwait(false);
            if (!TryReadIterationCount(output, out var iterations))
            {
                continue;
            }

            if (!AllIterationsSettled(flow, loopKey, iterations, view))
            {
                continue;
            }

            await runStore.RecordStepCompleteAsync(
                runId,
                loopKey,
                StepStatus.Succeeded.ToString(),
                JsonSerializer.Serialize(new { iterations }, _webOptions),
                null).ConfigureAwait(false);

            working ??= new Dictionary<string, StepStatus>(statuses, StringComparer.Ordinal);
            working[loopKey] = StepStatus.Succeeded;
            (settled ??= []).Add(loopKey);
        }

        return settled is null ? [] : settled;
    }

    /// <summary>
    /// Returns the runtime keys of every scoped step currently parked on its barrier,
    /// innermost first. Used by run recovery to settle loops whose last child completed
    /// while the host was down.
    /// </summary>
    /// <param name="flow">The flow being recovered.</param>
    /// <param name="statuses">Current runtime status map for the run.</param>
    public static IReadOnlyList<string> RunningLoopKeys(
        IFlowDefinition flow,
        IReadOnlyDictionary<string, StepStatus> statuses)
    {
        List<string>? running = null;
        foreach (var (stepKey, status) in statuses)
        {
            if (status != StepStatus.Running || flow.Manifest.Steps.FindStep(stepKey) is not IScopedStep)
            {
                continue;
            }

            (running ??= []).Add(stepKey);
        }

        if (running is null)
        {
            return [];
        }

        // Deepest scope first, mirroring EnclosingLoopKeys' ordering contract.
        running.Sort(static (left, right) => CountSegments(right).CompareTo(CountSegments(left)));
        return running;
    }

    private static bool IsTerminal(StepStatus status) =>
        status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Skipped;

    private static bool TryReadFromJson(JsonElement element, out int iterations)
    {
        iterations = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out iterations) && iterations >= 0;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, IterationCountProperty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out iterations)
                && iterations >= 0;
        }

        return false;
    }

    private static int CountSegments(string key) => key.AsSpan().Count('.');
}
