using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TokenAcquisitionService;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTokenAcquisitionService(this IServiceCollection services,
        TokenCacheOptions? options = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton(options ?? new TokenCacheOptions());
        services.AddSingleton<ITokenService, TokenService>();
        return services;
    }
}
