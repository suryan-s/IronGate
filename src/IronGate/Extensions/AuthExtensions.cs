using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace IronGate.Extensions;

public static class AuthExtensions
{
    public static IServiceCollection AddIronGateAuth(this IServiceCollection services, IConfiguration config)
    {
        var keycloak = config.GetSection("Keycloak");
        var authority = keycloak["Authority"];
        var audience = keycloak["Audience"];
        var requireHttpsMetadata = keycloak.GetValue<bool>("RequireHttpsMetadata");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = false, // admin-cli token has no aud claim; enable when using dedicated client
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(15)
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("jwt-required", policy => policy.RequireAuthenticatedUser());

        return services;
    }
}