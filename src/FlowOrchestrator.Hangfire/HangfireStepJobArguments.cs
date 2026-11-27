using System.Collections;
using System.Globalization;
using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Hangfire;

/// <summary>
/// Keeps a step job's <see cref="IStepInstance"/> argument intact through Hangfire's Newtonsoft.Json
/// argument store: <see cref="PrepareForEnqueue"/> on the dispatching side, <see cref="RestoreOnWorker"/>
/// on the worker.
/// </summary>
/// <remarks>
/// <para>
/// Newtonsoft.Json does not understand <see cref="JsonElement"/>: it writes the public <c>ValueKind</c>
/// property only and reads it back as <c>default(JsonElement)</c> (<see cref="JsonValueKind.Undefined"/>),
/// on which every read throws <see cref="InvalidOperationException"/>. A step re-dispatched after
/// <see cref="StepStatus.Pending"/> (a poll re-check, the <c>WaitForSignal</c> deadline check) carries the
/// inputs its attempt resolved, and those are <see cref="JsonElement"/> values: an expression over the
/// trigger body resolves to one, and a typed handler writes every input back as one. Up to 1.34.7 the
/// re-check therefore ran with destroyed inputs — a poll lost its inputs and stopped polling, a
/// <c>ForEach</c> child failed on its destroyed <c>__loopItem</c>, a <c>WaitForSignal</c> failed for a
/// missing <c>signalName</c>.
/// </para>
/// <para>
/// <see cref="PrepareForEnqueue"/> replaces every <see cref="JsonElement"/> in the inputs with the plain
/// CLR value Newtonsoft round-trips (string, <see cref="long"/> / <see cref="double"/>, bool, null,
/// <see cref="Dictionary{TKey, TValue}"/>, <see cref="List{T}"/>), the same shapes a <c>ForEach</c> item is
/// already dispatched as. <see cref="RestoreOnWorker"/> repairs what a job enqueued by 1.34.7 or earlier still
/// carries, so those jobs keep working after the upgrade.
/// </para>
/// </remarks>
internal static class HangfireStepJobArguments
{
    /// <summary>Input key under which <see cref="PollableStepHandler{TInput}"/> carries a poll's start time.</summary>
    internal const string PollStartedAtKey = "__pollStartedAtUtc";

    /// <summary>
    /// Returns the step instance to hand to Hangfire: <paramref name="step"/> itself when its inputs hold no
    /// <see cref="JsonElement"/>, otherwise a copy whose inputs hold the equivalent CLR values.
    /// </summary>
    /// <param name="step">The step the engine is dispatching; never mutated, the engine keeps using it.</param>
    /// <returns>A step instance whose inputs survive Newtonsoft.Json serialisation.</returns>
    /// <remarks>
    /// O(input leaves) per dispatch, allocation-free when there is nothing to convert — the case for every
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

        var converted = new Dictionary<string, object?>(inputs.Count, ComparerOf(inputs));
        foreach (var (key, value) in inputs)
        {
            converted[key] = ToNewtonsoftSafe(value);
        }

        return new StepInstance(step.Key, step.Type)
        {
            RunId = step.RunId,
            PrincipalId = step.PrincipalId,
            TriggerData = step.TriggerData,
            TriggerHeaders = step.TriggerHeaders,
            JobId = step.JobId,
            ScheduledTime = step.ScheduledTime,
            Inputs = converted,
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
    /// <returns>The number of inputs that were restored, dropped or normalised.</returns>
    /// <remarks>
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
    /// <para>
    /// Newtonsoft.Json reads an ISO-8601 string into an object slot as a <see cref="DateTime"/> in local
    /// time. For <c>__pollStartedAtUtc</c>, which <see cref="PollableStepHandler{TInput}"/> parses back, the
    /// value is turned back into the round-trip UTC string the handler wrote, so the poll window does not
    /// depend on the worker's time zone or culture.
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
        string? pollStartedAt = null;
        foreach (var (key, value) in inputs)
        {
            if (IsDestroyed(value))
            {
                (damaged ??= []).Add(key);
            }
            else if (string.Equals(key, PollStartedAtKey, StringComparison.Ordinal) && TryFormatRoundTrip(value, out var text))
            {
                pollStartedAt = text;
            }
        }

        if (damaged is null && pollStartedAt is null)
        {
            return 0;
        }

        var repaired = new Dictionary<string, object?>(inputs, ComparerOf(inputs));
        if (pollStartedAt is not null)
        {
            repaired[PollStartedAtKey] = pollStartedAt;
        }

        if (damaged is null)
        {
            step.Inputs = repaired;
            return 1;
        }

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

        step.Inputs = repaired;
        return damaged.Count + (pollStartedAt is null ? 0 : 1);
    }

    /// <summary>
    /// <see langword="true"/> for a value Newtonsoft.Json could not carry: an undefined
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

    /// <summary>
    /// Returns <paramref name="value"/> with every <see cref="JsonElement"/> in it, at any depth of
    /// dictionaries and sequences, replaced by its CLR equivalent.
    /// </summary>
    /// <param name="value">An input value.</param>
    /// <returns>The value itself when it holds no <see cref="JsonElement"/>; a converted copy otherwise.</returns>
    internal static object? ToNewtonsoftSafe(object? value)
    {
        switch (value)
        {
            case JsonElement element:
                return FromJsonElement(element);
            case null:
            case string:
                return value;
            case IDictionary<string, object?> dictionary:
                if (!ContainsJsonElement(dictionary))
                {
                    return value;
                }

                var converted = new Dictionary<string, object?>(dictionary.Count, ComparerOf(dictionary));
                foreach (var (key, item) in dictionary)
                {
                    converted[key] = ToNewtonsoftSafe(item);
                }

                return converted;
            case IEnumerable<object?> sequence:
                if (!ContainsJsonElement(sequence))
                {
                    return value;
                }

                var items = new List<object?>();
                foreach (var item in sequence)
                {
                    items.Add(ToNewtonsoftSafe(item));
                }

                return items.ToArray();
            default:
                return value;
        }
    }

    private static object? FromJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // Cast to object so the conditional keeps long vs double. Newtonsoft reads an integer back
                // as a long and anything else as a double, so these are the shapes that round-trip.
                return element.TryGetInt64(out var integer) ? integer : (object)element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object:
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    properties[property.Name] = FromJsonElement(property.Value);
                }

                return properties;
            case JsonValueKind.Array:
                var items = new List<object?>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FromJsonElement(item));
                }

                return items;
            default:
                // Null, and Undefined (nothing to carry).
                return null;
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

    private static bool ContainsJsonElement(IEnumerable<object?> sequence)
    {
        foreach (var item in sequence)
        {
            if (ContainsJsonElement(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsJsonElement(object? value) => value switch
    {
        JsonElement => true,
        null or string => false,
        IDictionary<string, object?> dictionary => ContainsJsonElement(dictionary),
        IEnumerable<object?> sequence => ContainsJsonElement(sequence),
        _ => false,
    };

    private static bool TryFormatRoundTrip(object? value, out string text)
    {
        switch (value)
        {
            case DateTime dateTime:
                var utc = dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
                text = new DateTimeOffset(utc).ToString("O", CultureInfo.InvariantCulture);
                return true;
            case DateTimeOffset dateTimeOffset:
                text = dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                return true;
            default:
                text = string.Empty;
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
