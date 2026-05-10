using System.Text.Json;
using FlowOrchestrator.Core.Expressions;

namespace FlowOrchestrator.Core.Execution.Internal;

/// <summary>
/// Asynchronous resolution of <c>@steps('key').output|status|error</c> expressions in a step's
/// input dictionary. Second pass of the input resolution pipeline used by
/// <see cref="DefaultStepExecutor"/>; runs after <see cref="InputResolutionPipeline"/>.
/// </summary>
internal static class StepExpressionResolutionPipeline
{
    /// <summary>
    /// Returns a dictionary with all <c>@steps()</c> expressions resolved by reading the
    /// referenced step's persisted output via <paramref name="resolver"/>.
    /// </summary>
    /// <param name="inputs">The input dictionary, already passed through <see cref="InputResolutionPipeline"/>.</param>
    /// <param name="resolver">The per-run output resolver bound to the active <c>RunId</c>.</param>
    public static async ValueTask<IDictionary<string, object?>> ResolveAsync(
        IDictionary<string, object?> inputs,
        StepOutputResolver resolver)
    {
        if (inputs.Count == 0)
            return inputs;

        var resolved = new Dictionary<string, object?>(inputs.Count);
        foreach (var (key, value) in inputs)
            resolved[key] = await ResolveValueAsync(value, resolver).ConfigureAwait(false);

        return resolved;
    }

    private static async ValueTask<object?> ResolveValueAsync(object? value, StepOutputResolver resolver)
    {
        switch (value)
        {
            case null:
                return null;

            case string s when StepOutputResolver.IsStepExpression(s):
                return await resolver.ResolveAsync(s).ConfigureAwait(false);

            case JsonElement { ValueKind: JsonValueKind.String } element:
            {
                var str = element.GetString();
                if (StepOutputResolver.IsStepExpression(str))
                    return await resolver.ResolveAsync(str!).ConfigureAwait(false);
                return value;
            }

            case IDictionary<string, object?> dict:
                return await ResolveAsync(dict, resolver).ConfigureAwait(false);

            case IEnumerable<object?> sequence:
            {
                var items = new List<object?>();
                foreach (var item in sequence)
                    items.Add(await ResolveValueAsync(item, resolver).ConfigureAwait(false));
                return items.ToArray();
            }

            default:
                return value;
        }
    }
}
