using IronGate.Domain;
using IronGate.Infrastructure.Caching;
using IronGate.Middleware;

namespace IronGate.Extensions;

public static class FallbackCacheExtensions
{
    public static IServiceCollection AddIronGateFallbackCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FallbackCacheOptions>(
            configuration.GetSection(FallbackCacheOptions.SectionName));

        services.AddMemoryCache();
        services.AddSingleton<IFallbackCacheStore, InMemoryFallbackCacheStore>();

        return services;
    }

    public static IApplicationBuilder UseIronGateFallbackCache(this IApplicationBuilder app)
    {
        return app.UseMiddleware<FallbackCacheMiddleware>();
    }
}