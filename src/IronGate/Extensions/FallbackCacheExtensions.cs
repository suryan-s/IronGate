using IronGate.Domain;
using IronGate.Infrastructure.Caching;
using IronGate.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

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

        // Register Redis connection for fallback cache (lazy - only used if enabled)
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var redisConfig = configuration.GetSection("FallbackCache:Redis").Get<RedisFallbackCacheOptions>() ?? new RedisFallbackCacheOptions();
            var config = ConfigurationOptions.Parse(redisConfig.ConnectionString);
            config.AbortOnConnectFail = false;
            config.ConnectRetry = 3;
            config.ConnectTimeout = 5000;
            return ConnectionMultiplexer.Connect(config);
        });

        // Register BOTH stores - factory will pick the right one at runtime
        services.AddSingleton<InMemoryFallbackCacheStore>();
        services.AddSingleton<RedisFallbackCacheStore>();

        return services;
    }

    public static IApplicationBuilder UseIronGateFallbackCache(this IApplicationBuilder app)
    {
        return app.UseMiddleware<FallbackCacheMiddleware>();
    }
}

/// <summary>
/// Configuration options for Redis fallback cache connection.
/// </summary>
public sealed class RedisFallbackCacheOptions
{
    public const string SectionName = "FallbackCache:Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
}