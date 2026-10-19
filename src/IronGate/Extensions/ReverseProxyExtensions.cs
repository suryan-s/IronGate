using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace IronGate.Extensions;

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddIronGateReverseProxy(this IServiceCollection services, IConfiguration config)
    {
        services
            .AddReverseProxy()
            .LoadFromConfig(config.GetSection("ReverseProxy"))
            .AddTransforms(builderContext =>
            {
                builderContext.AddRequestTransform(transformContext =>
                {
                    transformContext.ProxyRequest.Headers.Remove("X-IronGate-User-Id");
                    transformContext.ProxyRequest.Headers.Remove("X-IronGate-Username");

                    ClaimsPrincipal user = transformContext.HttpContext.User;
                    if (user.Identity?.IsAuthenticated != true) return ValueTask.CompletedTask;
                    // Try standard claims first, fall back to Keycloak-specific ones
                    string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                                     ?? user.FindFirstValue("sub")
                                     ?? user.FindFirstValue("azp"); // Keycloak: authorized party (client ID)
                    string? username = user.FindFirstValue("preferred_username")
                                       ?? user.FindFirstValue("scope"); // Fallback to scope

                    if (!string.IsNullOrEmpty(userId))
                    {
                        transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-IronGate-User-Id", userId);
                    }

                    if (!string.IsNullOrEmpty(username))
                    {
                        transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-IronGate-Username", username);
                    }

                    return ValueTask.CompletedTask;
                });
            });

        return services;
    }
}