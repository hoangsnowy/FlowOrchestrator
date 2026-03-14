using FlowOrchestrator.Core.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOrchestrator.Core.Configuration;

public static class StepHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddStepHandler<THandler>(this IServiceCollection services, string? typeName = null)
        where THandler : class
    {
        typeName ??= typeof(THandler).Name;

        services.AddTransient<THandler>();
        services.AddSingleton<IStepHandlerMetadata>(sp => new StepHandlerMetadata<THandler>(typeName!));

        return services;
    }
}
