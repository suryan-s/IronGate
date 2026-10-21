using IronGate.Middleware;
using IronGate.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IronGate.Extensions;

public static class LoadSheddingExtensions
{
    public static IServiceCollection AddIronGateLoadShedding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LoadSheddingOptions>(
            configuration.GetSection(LoadSheddingOptions.SectionName));

        return services;
    }

    public static IApplicationBuilder UseIronGateLoadShedding(this IApplicationBuilder app)
    {
        return app.UseMiddleware<LoadSheddingMiddleware>();
    }
}