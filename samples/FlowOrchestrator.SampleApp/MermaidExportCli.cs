using FlowOrchestrator.Core.Abstractions;
using FlowOrchestrator.Core.Diagnostics;

namespace FlowOrchestrator.SampleApp;

/// <summary>
/// CLI handler for <c>--export-mermaid &lt;flowId|flowName&gt;</c>. When the flag is present,
/// prints the Mermaid flowchart for the requested flow to stdout and signals the host
/// to exit without starting the web server. Useful for CI workflows that comment a
/// flow diagram on a pull request.
/// </summary>
internal static class MermaidExportCli
{
    private const string Flag = "--export-mermaid";

    /// <summary>
    /// Detects the <c>--export-mermaid</c> flag and, if present, writes the diagram and sets <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="args">Raw process arguments.</param>
    /// <param name="exitCode">Resulting process exit code: <c>0</c> on success, <c>1</c> when the flow was not found.</param>
    /// <returns><see langword="true"/> when the flag was handled and the host should exit; <see langword="false"/> to continue normal startup.</returns>
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (!TryGetTarget(args, out var target))
        {
            return false;
        }

        var flows = DiscoverFlows();
        var match = flows.FirstOrDefault(f =>
            string.Equals(f.GetType().Name, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Id.ToString(), target, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Console.Error.WriteLine($"Flow not found: '{target}'.");
            Console.Error.WriteLine("Available flows:");
            foreach (var f in flows)
            {
                Console.Error.WriteLine($"  {f.GetType().Name} ({f.Id})");
            }
            exitCode = 1;
            return true;
        }

        Console.WriteLine(match.ToMermaid());
        return true;
    }

    private static bool TryGetTarget(string[] args, out string target)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == Flag && i + 1 < args.Length)
            {
                target = args[i + 1];
                return true;
            }

            var prefix = Flag + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                target = arg[prefix.Length..];
                return true;
            }
        }

        target = string.Empty;
        return false;
    }

    /// <summary>
    /// Reflects over the sample app's assembly to instantiate every concrete
    /// <see cref="IFlowDefinition"/> with a public parameterless constructor.
    /// Eliminates the need to maintain a hardcoded list parallel to <c>options.AddFlow&lt;T&gt;()</c>.
    /// </summary>
    private static IReadOnlyList<IFlowDefinition> DiscoverFlows()
    {
        return typeof(MermaidExportCli).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                && !t.IsInterface
                && typeof(IFlowDefinition).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IFlowDefinition)Activator.CreateInstance(t)!)
            .OrderBy(f => f.GetType().Name, StringComparer.Ordinal)
            .ToList();
    }
}
