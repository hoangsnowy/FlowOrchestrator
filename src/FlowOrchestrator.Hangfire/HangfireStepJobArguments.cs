using System.Collections;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Keeps a step job's <see cref="IStepInstance"/> argument intact through Hangfire's job-argument store:
/// <see cref="PrepareForEnqueue"/> on the dispatching side, <see cref="RestoreOnWorker"/> on the worker.
/// </summary>
/// <remarks>
/// <para>
/// Hangfire stores job arguments with its own JSON serializer (Newtonsoft.Json), which does not understand
/// <see cref="JsonElement"/>: it keeps only the public <c>ValueKind</c> property and reads the value back as
/// <c>default(JsonElement)</c> (<see cref="JsonValueKind.Undefined"/>), on which every read throws
/// <see cref="InvalidOperationException"/>. A step re-dispatched after <see cref="StepStatus.Pending"/> (a
/// poll re-check, the <c>WaitForSignal</c> deadline check) carries the inputs its attempt resolved, and
/// those are <see cref="JsonElement"/> values: an expression over the trigger body resolves to one, and a
/// typed handler writes every input back as one. Up to 1.34.7 the re-check therefore ran with destroyed
/// inputs — a poll lost its inputs and stopped polling, a <c>ForEach</c> child failed on its destroyed
/// <c>__loopItem</c>, a <c>WaitForSignal</c> failed for a missing <c>signalName</c>.
/// </para>
/// <para>
/// <see cref="PrepareForEnqueue"/> moves every input that holds a <see cref="JsonElement"/> into one
/// System.Text.Json document carried as a plain string under <see cref="EncodedInputsKey"/>, so the job
/// argument holds only strings and primitives and nothing depends on how Hangfire's serializer treats
/// <see cref="JsonElement"/>, nested collections or date-like strings. <see cref="RestoreOnWorker"/> puts
/// the exact <see cref="JsonElement"/> values back, and repairs what a job enqueued by 1.34.7 or earlier
/// still carries, so those jobs keep working after the upgrade.
/// </para>
/// </remarks>
internal static class HangfireStepJobArguments
{
    /// <summary>
    /// Reserved input key carrying the System.Text.Json text of the inputs that held
    /// <see cref="JsonElement"/> values; removed again on the worker before the engine sees the step.
    /// </summary>
    internal const string EncodedInputsKey = "__hangfireJsonInputs";

    /// <summary>
    /// Returns the step instance to hand to Hangfire: <paramref name="step"/> itself when its inputs hold no
    /// <see cref="JsonElement"/>, otherwise a copy in which every input holding one has been moved into a
    /// System.Text.Json string under <see cref="EncodedInputsKey"/>.
    /// </summary>
    /// <param name="step">The step the engine is dispatching; never mutated, the engine keeps using it.</param>
    /// <returns>A step instance whose inputs are only values Hangfire's serializer carries intact.</returns>
    /// <remarks>
    /// O(input leaves) per dispatch, allocation-free when there is nothing to encode — the case for every
    /// first dispatch from the manifest. The trigger data is left as it is: the worker reloads it from the
    /// run's stored trigger data (see <see cref="RestoreOnWorker"/>).
    /// </remarks>
    public static IStepInstance PrepareForEnqueue(IStepInstance step)
    {
        var inputs = step.Inputs;
        if (inputs is null || inputs.Count == 0 || !ContainsJsonElement(inputs))
        {
            return step;
        }

        var plain = new Dictionary<string, object?>(inputs.Count, ComparerOf(inputs));
        var encoded = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in inputs)
        {
            if (!ContainsJsonElement(value))
            {
                plain[key] = value;
            }
            else if (IsDestroyed(value))
            {
                // Nothing left to carry (and System.Text.Json cannot write it); the worker restores it from
                // the manifest exactly as for a job enqueued by 1.34.7.
                plain[key] = null;
            }
            else
            {
                encoded[key] = value;
            }
        }

        if (encoded.Count > 0)
        {
            plain[EncodedInputsKey] = JsonSerializer.Serialize(encoded);
        }

        return new StepInstance(step.Key, step.Type)
        {
            RunId = step.RunId,
            PrincipalId = step.PrincipalId,
            TriggerData = step.TriggerData,
            TriggerHeaders = step.TriggerHeaders,
            JobId = step.JobId,
            ScheduledTime = step.ScheduledTime,
            Inputs = plain,
            Index = step.Index,
            ScopeMoveNext = step.ScopeMoveNext,
        };
    }

    /// <summary>
    /// Repairs <paramref name="ctx"/> and <paramref name="step"/> in place after Hangfire deserialised them,
    /// before the engine runs the step.
    /// </summary>
    /// <param name="ctx">The execution context deserialised from the job.</param>
    /// <param name="step">The step instance deserialised from the job.</param>
    /// <param name="flow">The registered flow the step belongs to, whose manifest supplies replacements.</param>
    /// <returns>The number of inputs that were decoded, restored or dropped.</returns>
    /// <remarks>
    /// <para>
    /// Inputs carried under <see cref="EncodedInputsKey"/> are decoded back into the
    /// <see cref="JsonElement"/> values the dispatching side resolved, and the reserved key is removed. A
    /// dictionary or sequence that mixed <see cref="JsonElement"/> leaves with CLR values comes back as one
    /// <see cref="JsonElement"/> object or array; input resolution and typed input binding treat both alike.
    /// </para>
    /// <para>
    /// Destroyed trigger data (every 1.x job carries some) is cleared; the engine reloads it from the run's
    /// stored trigger data and fills the step's copy from the context.
    /// </para>
    /// <para>
    /// A destroyed input — only a job enqueued by 1.34.7 or earlier has one — is replaced by the step's
    /// manifest value under the manifest's spelling of the key, so a manifest expression resolves again from
    /// the run's stored trigger data and step outputs exactly as on a first attempt. A destroyed input the
    /// manifest does not declare is dropped: the engine recomputes <c>__loopItem</c> from the loop source,
    /// and poll bookkeeping (<c>__pollAttempt</c>, <c>__pollStartedAtUtc</c>) restarts, so such a poll
    /// begins a fresh attempt count and <c>pollTimeoutSeconds</c> window.
    /// </para>
    /// <para>Complexity: O(input leaves) per step job; nothing is allocated when nothing needs repair.</para>
    /// </remarks>
    public static int RestoreOnWorker(IExecutionContext ctx, IStepInstance step, IFlowDefinition flow)
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

        var hasEncoded = inputs.TryGetValue(EncodedInputsKey, out var encodedValue);
        if (damaged is null && !hasEncoded)
        {
            return 0;
        }

        var repaired = new Dictionary<string, object?>(inputs, ComparerOf(inputs));
        var changed = 0;

        if (damaged is not null)
        {
            var manifestInputs = flow.Manifest.Steps.FindStep(step.Key)?.Inputs;
            foreach (var key in damaged)
            {
                repaired.Remove(key);
                if (TryGetManifestValue(manifestInputs, key, out var manifestKey, out var manifestValue))
                {
                    // Re-key under the manifest's spelling: the resolver matches manifest text by key, and a
                    // typed handler wrote its inputs back under its serializer's property names.
                    repaired[manifestKey] = manifestValue;
                }
            }

            changed += damaged.Count;
        }

        if (hasEncoded)
        {
            repaired.Remove(EncodedInputsKey);
            if (encodedValue is string json && json.Length > 0)
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    repaired[property.Name] = property.Value.Clone();
                    changed++;
                }
            }
        }

        step.Inputs = repaired;
        return changed;
    }

    /// <summary>
    /// <see langword="true"/> for a value Hangfire's serializer could not carry: an undefined
    /// <see cref="JsonElement"/>, or a collection holding one at any depth.
    /// </summary>
    /// <param name="value">A value deserialised from a Hangfire job argument.</param>
    internal static bool IsDestroyed(object? value)
    {
        switch (value)
        {
            case JsonElement element:
                return element.ValueKind == JsonValueKind.Undefined;
            case null:
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

    private static bool ContainsJsonElement(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        foreach (var (_, value) in entries)
        {
            if (ContainsJsonElement(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsJsonElement(object? value)
    {
        switch (value)
        {
            case JsonElement:
                return true;
            case null:
            case string:
                return false;
            case IDictionary<string, object?> dictionary:
                return ContainsJsonElement(dictionary);
            case IEnumerable<object?> sequence:
                foreach (var item in sequence)
                {
                    if (ContainsJsonElement(item))
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

    private static IEqualityComparer<string> ComparerOf(IDictionary<string, object?> dictionary)
        => dictionary is Dictionary<string, object?> concrete ? concrete.Comparer : StringComparer.Ordinal;
}
