using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using IronGate.Domain;

namespace IronGate.Infrastructure.Keycloak;

/// <summary>
/// Keycloak-specific identity provider adapter.
/// Handles Keycloak's claim format (realm_access, resource_access, preferred_username, etc.)
/// </summary>
public sealed class KeycloakIdentityProvider : IdentityProviderBase
{
    public override string Name => "keycloak";
    public override string ConfigSectionName => "Keycloak";

    public override void ConfigureAuthentication(JwtBearerOptions options, IConfigurationSection config)
    {
        base.ConfigureAuthentication(options, config);
        
        // Keycloak-specific: set metadata address if not using standard discovery
        string? metadataAddress = config["MetadataAddress"];
        if (!string.IsNullOrEmpty(metadataAddress))
        {
            options.MetadataAddress = metadataAddress;
        }
    }

    public override string? GetUserId(ClaimsPrincipal user)
    {
        // Keycloak: sub claim is the user ID, azp is the client ID (fallback)
        return user.FindFirstValue("sub")
            ?? user.FindFirstValue("azp")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public override string? GetUsername(ClaimsPrincipal user)
    {
        // Keycloak: preferred_username is the standard claim
        return user.FindFirstValue("preferred_username")
            ?? user.FindFirstValue("username")
            ?? user.FindFirstValue(ClaimTypes.Name);
    }

    public override IReadOnlyList<string> GetRoles(ClaimsPrincipal user)
    {
        var roles = new List<string>();

        // Keycloak stores roles in realm_access.roles (realm-level) and resource_access.{client}.roles (client-level)
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

        return roles.Distinct().ToList();
    }

    public override void MapClaims(ClaimsIdentity? identity)
    {
        if (identity == null) return;

        // Keycloak: Map realm_access/resource_access to standard role claims for easier access
        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (!string.IsNullOrEmpty(realmAccess))
        {
            try
            {
                using var doc = JsonDocument.Parse(realmAccess);
                if (doc.RootElement.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var role in rolesElement.EnumerateArray())
                    {
                        var roleValue = role.GetString();
                        if (!string.IsNullOrEmpty(roleValue))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, roleValue));
                        }
                    }
                }
            }
            catch { }
        }

        // Also map resource_access roles
        var resourceAccess = identity.FindFirst("resource_access")?.Value;
        if (!string.IsNullOrEmpty(resourceAccess))
        {
            try
            {
                using var doc = JsonDocument.Parse(resourceAccess);
                foreach (var client in doc.RootElement.EnumerateObject())
                {
                    if (client.Value.TryGetProperty("roles", out var clientRoles) && clientRoles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var role in clientRoles.EnumerateArray())
                        {
                            var roleValue = role.GetString();
                            if (!string.IsNullOrEmpty(roleValue))
                            {
                                identity.AddClaim(new Claim(ClaimTypes.Role, roleValue));
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}