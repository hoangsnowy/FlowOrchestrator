using System.Text.Json;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Execution;

namespace FlowOrchestrator.Core.Configuration.Internal;

/// <summary>
/// Wraps an untyped <see cref="IStepInstance"/> and exposes a strongly-typed
/// <typeparamref name="TInput"/> <see cref="Inputs"/> property for use by
/// <see cref="IStepHandler{TInput}"/> implementations. Syncs mutations back to the
/// inner instance after execution so the output store receives the final input state.
/// </summary>
/// <typeparam name="TInput">The deserialized input model type.</typeparam>
internal sealed class TypedStepInstanceAdapter<TInput> : IStepInstance<TInput>
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IStepInstance _inner;

    /// <summary>Initialises the adapter wrapping <paramref name="inner"/> with pre-deserialized <paramref name="inputs"/>.</summary>
    /// <param name="inner">The underlying untyped step instance.</param>
    /// <param name="inputs">The deserialized input model.</param>
    public TypedStepInstanceAdapter(IStepInstance inner, TInput inputs)
    {
        _inner = inner;
        Inputs = inputs;
    }

    /// <inheritdoc/>
    public Guid RunId
    {
        get => _inner.RunId;
        set => _inner.RunId = value;
    }

    /// <inheritdoc/>
    public string? PrincipalId
    {
        get => _inner.PrincipalId;
        set => _inner.PrincipalId = value;
    }

    /// <inheritdoc/>
    public object? TriggerData
    {
        get => _inner.TriggerData;
        set => _inner.TriggerData = value;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? TriggerHeaders
    {
        get => _inner.TriggerHeaders;
        set => _inner.TriggerHeaders = value;
    }

    /// <inheritdoc/>
    public string? JobId
    {
        get => _inner.JobId;
        set => _inner.JobId = value;
    }

    /// <inheritdoc/>
    public DateTimeOffset ScheduledTime
    {
        get => _inner.ScheduledTime;
        set => _inner.ScheduledTime = value;
    }

    /// <inheritdoc/>
    public string Type
    {
        get => _inner.Type;
        set => _inner.Type = value;
    }

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public TInput Inputs { get; set; }

    /// <inheritdoc/>
    public int Index
    {
        get => _inner.Index;
        set => _inner.Index = value;
    }

    /// <inheritdoc/>
    public bool ScopeMoveNext
    {
        get => _inner.ScopeMoveNext;
        set => _inner.ScopeMoveNext = value;
    }

    /// <summary>
    /// Serialises the strongly-typed <see cref="Inputs"/> back onto the wrapped
    /// step instance's untyped input dictionary so handler-side mutations are
    /// observable by the output store and downstream steps.
    /// </summary>
    /// <param name="options">Serializer options to use; falls back to a Web-defaults singleton when <see langword="null"/>.</param>
    internal void SyncInputsToInner(JsonSerializerOptions? options = null)
    {
        if (Inputs is IDictionary<string, object?> dict)
        {
            _inner.Inputs = new Dictionary<string, object?>(dict);
            return;
        }

        if (Inputs is null)
        {
            _inner.Inputs = new Dictionary<string, object?>();
            return;
        }

        var serializerOptions = options ?? _jsonOptions;
        var serialized = JsonSerializer.SerializeToElement(Inputs, Inputs.GetType(), serializerOptions);
        if (serialized.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var converted = JsonSerializer.Deserialize<Dictionary<string, object?>>(serialized.GetRawText(), serializerOptions)
            ?? new Dictionary<string, object?>();
        _inner.Inputs = converted;
    }
}
