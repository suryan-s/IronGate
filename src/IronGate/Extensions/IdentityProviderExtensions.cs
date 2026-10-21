using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using IronGate.Domain;
using IronGate.Infrastructure.Keycloak;

namespace IronGate.Extensions;

/// <summary>
/// Extension methods for registering IDP services.
/// </summary>
public static class IdentityProviderExtensions
{
    public static IServiceCollection AddIdentityProviders(this IServiceCollection services, IConfiguration config)
    {
        var registry = new IdentityProviderRegistry();

        // Register built-in Keycloak adapter
        var keycloak = new KeycloakIdentityProvider();
        registry.Register(keycloak);

        services.AddSingleton<IIdentityProviderRegistry>(registry);
        services.AddSingleton(keycloak); // Also register concrete type for direct access if needed

        return services;
    }

    /// <summary>
    /// Internal method to configure authentication using the registered IDP.
    /// Reads "IdentityProvider:Default" from config to select which provider to use.
    /// </summary>
    internal static IServiceCollection AddIronGateAuthInternal(this IServiceCollection services, IConfiguration config)
    {
        // Build a temporary provider to get the registry
        var tempProvider = services.BuildServiceProvider();
        var registry = tempProvider.GetRequiredService<IIdentityProviderRegistry>();

        // Determine which IDP to use (default to "keycloak" for backward compatibility)
        string idpName = config["IdentityProvider:Default"] ?? "keycloak";
        var provider = registry.GetRequiredProvider(idpName);

        var configSection = config.GetSection(provider.ConfigSectionName);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                provider.ConfigureAuthentication(options, configSection);

                options.TokenValidationParameters = new TokenValidationParameters();
                provider.ConfigureTokenValidation(options.TokenValidationParameters, configSection);

                // Hook for IDP-specific claim mapping after validation
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        provider.MapClaims(context.Principal!.Identity as ClaimsIdentity);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("jwt-required", policy => policy.RequireAuthenticatedUser());

        return services;
    }
}