using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using IronGate.Domain;

namespace IronGate.Extensions;

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddIronGateReverseProxy(this IServiceCollection services, IConfiguration config)
    {
        // Build a temporary provider to get the IDP registry
        var tempProvider = services.BuildServiceProvider();
        var registry = tempProvider.GetRequiredService<IIdentityProviderRegistry>();

        string idpName = config["IdentityProvider:Default"] ?? "keycloak";
        var provider = registry.GetRequiredProvider(idpName);

        // Get downstream security secret
        var downstreamSecret = config["DownstreamSecurity:SharedSecret"];

        services
            .AddReverseProxy()
            .LoadFromConfig(config.GetSection("ReverseProxy"))
            .AddTransforms(builderContext =>
            {
                builderContext.AddRequestTransform(transformContext =>
                {
                    // SECURITY: Always strip any incoming gateway secret header from external clients
                    transformContext.ProxyRequest.Headers.Remove("X-IronGate-Gateway-Secret");

                    // Inject gateway secret if configured (zero-trust verification)
                    if (!string.IsNullOrEmpty(downstreamSecret))
                    {
                        transformContext.ProxyRequest.Headers.TryAddWithoutValidation(
                            "X-IronGate-Gateway-Secret", downstreamSecret);
                    }

                    transformContext.ProxyRequest.Headers.Remove("X-IronGate-User-Id");
                    transformContext.ProxyRequest.Headers.Remove("X-IronGate-Username");

                    ClaimsPrincipal user = transformContext.HttpContext.User;
                    if (user.Identity?.IsAuthenticated != true) return ValueTask.CompletedTask;

                    // Use IDP adapter for claim extraction
                    string? userId = provider.GetUserId(user);
                    string? username = provider.GetUsername(user);

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

                // Also add response transforms to echo user info back to client
                builderContext.AddResponseTransform(transformContext =>
                {
                    ClaimsPrincipal user = transformContext.HttpContext.User;
                    if (user.Identity?.IsAuthenticated != true) return ValueTask.CompletedTask;

                    string? userId = provider.GetUserId(user);
                    string? username = provider.GetUsername(user);
                    var roles = provider.GetRoles(user);

                    if (!string.IsNullOrEmpty(userId))
                    {
                        transformContext.HttpContext.Response.Headers.TryAdd("x-forwarded-user", userId);
                    }

                    if (!string.IsNullOrEmpty(username))
                    {
                        transformContext.HttpContext.Response.Headers.TryAdd("x-forwarded-username", username);
                    }

                    if (roles.Count > 0)
                    {
                        transformContext.HttpContext.Response.Headers.TryAdd("x-forwarded-roles", string.Join(",", roles.Distinct()));
                    }

                    return ValueTask.CompletedTask;
                });
            });

        return services;
    }
}