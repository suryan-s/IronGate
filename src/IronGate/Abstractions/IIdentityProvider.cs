using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace IronGate.Abstractions;

/// <summary>
/// Abstracts identity provider configuration and claim mapping.
/// Allows swapping Keycloak, Auth0, Azure AD, etc. without changing gateway code.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Unique name for this IDP (e.g., "keycloak", "auth0", "azure-ad")</summary>
    string Name { get; }

    /// <summary>Configuration section name in appsettings (e.g., "Keycloak", "Auth0")</summary>
    string ConfigSectionName { get; }

    /// <summary>Configure JWT Bearer authentication for this IDP</summary>
    void ConfigureAuthentication(JwtBearerOptions options, IConfigurationSection config);

    /// <summary>Configure TokenValidationParameters for this IDP</summary>
    void ConfigureTokenValidation(TokenValidationParameters parameters, IConfigurationSection config);

    /// <summary>
    /// Extract user ID from claims principal using IDP-specific claim mappings.
    /// Returns null if not found.
    /// </summary>
    string? GetUserId(ClaimsPrincipal user);

    /// <summary>
    /// Extract username/preferred_username from claims principal using IDP-specific claim mappings.
    /// Returns null if not found.
    /// </summary>
    string? GetUsername(ClaimsPrincipal user);

    /// <summary>
    /// Extract roles from claims principal using IDP-specific claim mappings.
    /// Returns empty list if not found.
    /// </summary>
    IReadOnlyList<string> GetRoles(ClaimsPrincipal user);

    /// <summary>
    /// Optional: Map IDP-specific claims to standard claims after token validation.
    /// Called during token validation events.
    /// </summary>
    void MapClaims(ClaimsIdentity? identity);
}

/// <summary>
/// Base implementation with common defaults. Override only what's needed.
/// </summary>
public abstract class IdentityProviderBase : IIdentityProvider
{
    public abstract string Name { get; }
    public abstract string ConfigSectionName { get; }

    public virtual void ConfigureAuthentication(JwtBearerOptions options, IConfigurationSection config)
    {
        options.Authority = config["Authority"];
        options.Audience = config["Audience"];
        options.RequireHttpsMetadata = config.GetValue<bool>("RequireHttpsMetadata");
    }

    public virtual void ConfigureTokenValidation(TokenValidationParameters parameters, IConfigurationSection config)
    {
        parameters.ValidateIssuer = true;
        parameters.ValidateAudience = true;
        parameters.ValidateLifetime = true;
        parameters.ClockSkew = TimeSpan.FromSeconds(15);
    }

    public virtual string? GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");
    }

    public virtual string? GetUsername(ClaimsPrincipal user)
    {
        return user.FindFirstValue("preferred_username")
            ?? user.FindFirstValue(ClaimTypes.Name);
    }

    public virtual IReadOnlyList<string> GetRoles(ClaimsPrincipal user)
    {
        var roles = new List<string>();
        roles.AddRange(user.FindAll(ClaimTypes.Role).Select(c => c.Value));
        roles.AddRange(user.FindAll("roles").Select(c => c.Value));
        return roles.Distinct().ToList();
    }

    public virtual void MapClaims(ClaimsIdentity? identity)
    {
        // Override in derived classes for IDP-specific claim mapping
    }
}

/// <summary>
/// Registry for IDP adapters. Allows multiple IDPs to be registered and selected by name.
/// </summary>
public interface IIdentityProviderRegistry
{
    IIdentityProvider? GetProvider(string name);
    IIdentityProvider GetRequiredProvider(string name);
    IEnumerable<IIdentityProvider> GetAllProviders();
    void Register(IIdentityProvider provider);
}

/// <summary>
/// Default implementation of IDP registry.
/// </summary>
public sealed class IdentityProviderRegistry : IIdentityProviderRegistry
{
    private readonly Dictionary<string, IIdentityProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IIdentityProvider provider)
    {
        _providers[provider.Name] = provider;
    }

    public IIdentityProvider? GetProvider(string name)
    {
        _providers.TryGetValue(name, out var provider);
        return provider;
    }

    public IIdentityProvider GetRequiredProvider(string name)
    {
        return GetProvider(name) ?? throw new InvalidOperationException($"Identity provider '{name}' not registered.");
    }

    public IEnumerable<IIdentityProvider> GetAllProviders() => _providers.Values;
}