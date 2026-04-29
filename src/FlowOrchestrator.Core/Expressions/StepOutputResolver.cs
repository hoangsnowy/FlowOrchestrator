using System.Text.Json;
using System.Text.RegularExpressions;
using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Storage;

namespace FlowOrchestrator.Core.Expressions;

/// <summary>
/// Resolves <c>@steps('key').output.field</c>, <c>@steps('key').status</c>, and
/// <c>@steps('key').error</c> expressions against the persisted outputs of prior steps
/// in the current run.
/// </summary>
/// <remarks>
/// Instances are scoped to a single step execution. Step output fetches are cached per
/// step key so that multiple expressions referencing the same prior step incur only one
/// <see cref="IOutputsRepository.GetStepOutputAsync"/> call.
/// </remarks>
public sealed class StepOutputResolver
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Matches:  @steps('key').output|status|error  followed by an optional path trail
    // Both single and double quotes are accepted for the step key.
    private static readonly Regex _pattern = new(
        @"^@steps\(['""]([^'""]+)['""]\)\.(output|status|error)(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private readonly IOutputsRepository _outputsRepository;
    private readonly IFlowRunStore _runStore;
    private readonly Guid _runId;
    private readonly StepCollection _steps;

    // Per-execution output cache — avoids duplicate GetStepOutputAsync calls for the same step key.
    private readonly Dictionary<string, object?> _outputCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outputLoaded = new(StringComparer.Ordinal);

    // Lazy run-detail cache for .status and .error — loaded at most once per resolver instance.
    private FlowRunRecord? _runDetail;
    private bool _runDetailLoaded;

    /// <summary>
    /// Initialises a resolver scoped to a single step execution.
    /// </summary>
    /// <param name="outputsRepository">Used to fetch outputs of prior steps.</param>
    /// <param name="runStore">Used to fetch step status and error message for <c>.status</c> and <c>.error</c> access.</param>
    /// <param name="runId">The identifier of the current flow run.</param>
    /// <param name="steps">
    /// The flow's step collection, used to validate that any referenced step key exists in the manifest.
    /// </param>
    public StepOutputResolver(
        IOutputsRepository outputsRepository,
        IFlowRunStore runStore,
        Guid runId,
        StepCollection steps)
    {
        _outputsRepository = outputsRepository;
        _runStore = runStore;
        _runId = runId;
        _steps = steps;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="expression"/> begins with <c>@steps(</c>.
    /// </summary>
    public static bool IsStepExpression(string? expression) =>
        expression?.TrimStart().StartsWith("@steps(", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Resolves the expression and returns the target value.
    /// </summary>
    /// <param name="expression">The raw expression string, e.g. <c>@steps('fetch').output.orderId</c>.</param>
    /// <returns>
    /// The resolved value, or <see langword="null"/> when the referenced step has not yet produced output.
    /// Returns <paramref name="expression"/> unchanged when it does not match the <c>@steps()</c> pattern.
    /// </returns>
    /// <exception cref="FlowExpressionException">
    /// Thrown when the step key in the expression is not declared in the flow manifest.
    /// </exception>
    public async ValueTask<object?> ResolveAsync(string expression)
    {
        var trimmed = expression.Trim();
        var match = _pattern.Match(trimmed);
        if (!match.Success)
            return expression; // passthrough — not a @steps() expression

        var stepKey = match.Groups[1].Value;
        var property = match.Groups[2].Value.ToLowerInvariant();
        var trail = match.Groups[3].Value;

        if (_steps.FindStep(stepKey) is null)
            throw new FlowExpressionException(
                expression,
                stepKey,
                $"Step '{stepKey}' is not defined in the flow manifest. Expression: '{expression}'");

        return property switch
        {
            "output" => await ResolveOutputAsync(stepKey, trail).ConfigureAwait(false),
            "status" => await ResolveStatusAsync(stepKey).ConfigureAwait(false),
            "error" => await ResolveErrorAsync(stepKey).ConfigureAwait(false),
            _ => null
        };
    }

    private async ValueTask<object?> ResolveOutputAsync(string stepKey, string trail)
    {
        if (!_outputLoaded.Contains(stepKey))
        {
            _outputCache[stepKey] = await _outputsRepository.GetStepOutputAsync(_runId, stepKey).ConfigureAwait(false);
            _outputLoaded.Add(stepKey);
        }

        var output = _outputCache.GetValueOrDefault(stepKey);
        if (output is null)
            return null;

        if (string.IsNullOrEmpty(trail))
            return ExpressionPathHelper.ToJsonElement(output, _jsonOptions);

        // Strip leading . or ?. from the trail before path resolution.
        var path = trail.Trim();
        if (path.StartsWith("?.", StringComparison.Ordinal)) path = path[2..];
        else if (path.StartsWith(".", StringComparison.Ordinal)) path = path[1..];

        if (string.IsNullOrEmpty(path))
            return ExpressionPathHelper.ToJsonElement(output, _jsonOptions);

        var element = ExpressionPathHelper.ToJsonElement(output, _jsonOptions);
        return ExpressionPathHelper.TryResolvePath(element, path, out var target) ? target : null;
    }

    private async ValueTask<object?> ResolveStatusAsync(string stepKey)
    {
        var detail = await GetRunDetailAsync().ConfigureAwait(false);
        var record = detail?.Steps?.FirstOrDefault(
            s => string.Equals(s.StepKey, stepKey, StringComparison.Ordinal));
        return record?.Status;
    }

    private async ValueTask<object?> ResolveErrorAsync(string stepKey)
    {
        var detail = await GetRunDetailAsync().ConfigureAwait(false);
        var record = detail?.Steps?.FirstOrDefault(
            s => string.Equals(s.StepKey, stepKey, StringComparison.Ordinal));
        return record?.ErrorMessage;
    }

    private async ValueTask<FlowRunRecord?> GetRunDetailAsync()
    {
        if (!_runDetailLoaded)
        {
            _runDetail = await _runStore.GetRunDetailAsync(_runId).ConfigureAwait(false);
            _runDetailLoaded = true;
        }
        return _runDetail;
    }
}
