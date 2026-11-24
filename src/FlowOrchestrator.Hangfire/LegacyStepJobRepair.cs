using System.Collections;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Repairs the arguments of a Hangfire step job enqueued by FlowOrchestrator 1.17 – 1.34.x before the
/// engine runs it.
/// </summary>
/// <remarks>
/// <para>
/// Those jobs carried the execution context and the step instance as interface-typed arguments, which
/// Hangfire stores with Newtonsoft.Json. A <see cref="JsonElement"/> does not survive that round trip:
/// Newtonsoft writes its public <c>ValueKind</c> property only and reads it back as
/// <c>default(JsonElement)</c> (<see cref="JsonValueKind.Undefined"/>), on which every read throws.
/// 1.x put <see cref="JsonElement"/> values into the job whenever a step was re-dispatched with its
/// resolved inputs — a <see cref="StepStatus.Pending"/> poll re-check, the <c>WaitForSignal</c>
/// deadline check, a <c>ForEach</c> item resolved from the trigger body — and into the context's
/// trigger data on every job.
/// </para>
/// <para>
/// A destroyed top-level input is replaced by the step's manifest value under the same key, so a
/// manifest expression is resolved again from the run's stored trigger data and step outputs exactly as
/// on a first attempt. A destroyed input the manifest does not declare is dropped: <c>__loopItem</c>
/// is then recomputed by the engine from the loop source, and poll bookkeeping
/// (<c>__pollAttempt</c>, <c>__pollStartedAtUtc</c>) restarts, so a poll queued by 1.x starts a
/// fresh attempt count and timeout window on its first 2.0 run. Destroyed trigger data is cleared and
/// reloaded by the engine from the run's stored trigger data.
/// </para>
/// <para>Complexity: O(input leaves) per legacy job, once; new 2.0 jobs never come through here.</para>
/// </remarks>
internal static class LegacyStepJobRepair
{
    /// <summary>Repairs <paramref name="ctx"/> and <paramref name="step"/> in place.</summary>
    /// <param name="ctx">The execution context deserialised from the legacy job.</param>
    /// <param name="step">The step instance deserialised from the legacy job.</param>
    /// <param name="flow">The registered flow the step belongs to, whose manifest supplies replacements.</param>
    /// <returns>The number of inputs that were replaced or dropped.</returns>
    public static int Repair(IExecutionContext ctx, IStepInstance step, IFlowDefinition flow)
    {
        if (IsDestroyed(ctx.TriggerData))
        {
            ctx.TriggerData = null;
        }
        if (IsDestroyed(step.TriggerData))
        {
            step.TriggerData = null;
        }

        var inputs = step.Inputs;
        if (inputs is null || inputs.Count == 0)
        {
            return 0;
        }

        List<string>? damaged = null;
        foreach (var (key, value) in inputs)
        {
            if (IsDestroyed(value))
            {
                (damaged ??= []).Add(key);
            }
        }
        if (damaged is null)
        {
            return 0;
        }

        var manifestInputs = flow.Manifest.Steps.FindStep(step.Key)?.Inputs;
        var repaired = new Dictionary<string, object?>(inputs, StringComparer.Ordinal);
        foreach (var key in damaged)
        {
            if (TryGetManifestValue(manifestInputs, key, out var manifestKey, out var manifestValue))
            {
                // Re-key under the manifest's spelling: the resolver matches manifest text by key, and a
                // typed handler wrote its inputs back under its serializer's property names.
                repaired.Remove(key);
                repaired[manifestKey] = manifestValue;
            }
            else
            {
                repaired.Remove(key);
            }
        }

        step.Inputs = repaired;
        return damaged.Count;
    }

    /// <summary>
    /// <see langword="true"/> for a value Newtonsoft.Json could not carry: an undefined
    /// <see cref="JsonElement"/>, or a collection holding one at any depth.
    /// </summary>
    internal static bool IsDestroyed(object? value)
    {
        switch (value)
        {
            case JsonElement element:
                return element.ValueKind == JsonValueKind.Undefined;
            case string:
                return false;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (IsDestroyed(entry.Value))
                    {
                        return true;
                    }
                }
                return false;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    if (IsDestroyed(item))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static bool TryGetManifestValue(
        IDictionary<string, object?>? manifestInputs,
        string key,
        out string manifestKey,
        out object? value)
    {
        manifestKey = key;
        value = null;
        if (manifestInputs is null)
        {
            return false;
        }
        if (manifestInputs.TryGetValue(key, out value))
        {
            return true;
        }
        foreach (var (candidate, candidateValue) in manifestInputs)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                manifestKey = candidate;
                value = candidateValue;
                return true;
            }
        }
        return false;
    }
}
