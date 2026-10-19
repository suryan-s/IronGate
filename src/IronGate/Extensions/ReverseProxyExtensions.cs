using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;
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

                // Also add response transforms to echo user info back to client
                builderContext.AddResponseTransform(transformContext =>
                {
                    ClaimsPrincipal user = transformContext.HttpContext.User;
                    if (user.Identity?.IsAuthenticated != true) return ValueTask.CompletedTask;

                    string? userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                                     ?? user.FindFirstValue("sub")
                                     ?? user.FindFirstValue("azp");
                    string? username = user.FindFirstValue("preferred_username")
                                       ?? user.FindFirstValue("scope");
                    
                    // Extract roles from realm_access and resource_access (Keycloak format)
                    var roles = new List<string>();
                    var realmAccess = user.FindFirst("realm_access")?.Value;
                    if (!string.IsNullOrEmpty(realmAccess))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(realmAccess);
                            if (doc.RootElement.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
                            {
                                roles.AddRange(rolesElement.EnumerateArray().Select(r => r.GetString()!).Where(r => !string.IsNullOrEmpty(r)));
                            }
                        }
                        catch { }
                    }
                    var resourceAccess = user.FindFirst("resource_access")?.Value;
                    if (!string.IsNullOrEmpty(resourceAccess))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(resourceAccess);
                            foreach (var client in doc.RootElement.EnumerateObject())
                            {
                                if (client.Value.TryGetProperty("roles", out var clientRoles) && clientRoles.ValueKind == JsonValueKind.Array)
                                {
                                    roles.AddRange(clientRoles.EnumerateArray().Select(r => r.GetString()!).Where(r => !string.IsNullOrEmpty(r)));
                                }
                            }
                        }
                        catch { }
                    }
                    // Fallback to standard role claims
                    if (roles.Count == 0)
                    {
                        roles.AddRange(user.FindAll(ClaimTypes.Role).Select(c => c.Value));
                        roles.AddRange(user.FindAll("roles").Select(c => c.Value));
                    }

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