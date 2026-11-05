using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TokenAcquisitionService;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTokenAcquisitionService(this IServiceCollection services,
        TokenCacheOptions? options = null, ResilienceOptions? resilience = null,
        DistributedConfiguration? distributed = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddLogging();
        services.AddSingleton(options ?? new TokenCacheOptions());
        services.AddSingleton(resilience ?? new ResilienceOptions());
        if (distributed is not null) services.AddSingleton(distributed);
        services.AddSingleton<ITokenService, TokenService>();
        return services;
    }
}
