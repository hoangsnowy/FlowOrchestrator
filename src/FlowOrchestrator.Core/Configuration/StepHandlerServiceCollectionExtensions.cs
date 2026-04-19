using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Core.Configuration;

/// <summary>
/// DI registration helpers for step handler types.
/// </summary>
public static class StepHandlerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> as a transient service and associates it with
    /// <paramref name="typeName"/> so the orchestrator can resolve and invoke it by the step's
    /// <c>"type"</c> field in the flow manifest.
    /// </summary>
    /// <typeparam name="THandler">
    /// The handler class. Must implement <see cref="IStepHandler"/> or <see cref="IStepHandler{TInput}"/>.
    /// </typeparam>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="typeName">
    /// The logical type name matching the <c>"type"</c> field in the manifest.
    /// Defaults to the class name of <typeparamref name="THandler"/> when omitted.
    /// </param>
    public static IServiceCollection AddStepHandler<THandler>(this IServiceCollection services, string? typeName = null)
        where THandler : class
    {
        typeName ??= typeof(THandler).Name;

        services.AddTransient<THandler>();
        services.AddSingleton<IStepHandlerMetadata>(sp => new StepHandlerMetadata<THandler>(typeName!));

        return services;
    }
}
