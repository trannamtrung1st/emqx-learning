using Microsoft.Extensions.DependencyInjection;
using EmqxLearning.Shared.Diagnostic;
using EmqxLearning.Shared.Diagnostic.Abstracts;

namespace EmqxLearning.Shared.Extensions;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddResourceMonitor(this IServiceCollection services)
    {
        return services.AddSingleton<IResourceMonitor, ResourceMonitor>();
    }

    public static IServiceCollection AddRateMonitor(this IServiceCollection services)
    {
        return services.AddSingleton<IRateMonitor, RateMonitor>();
    }
}
