using System.Security.Claims;
using System.Collections.Generic;
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
        List<string> roles = new List<string>();

        // Keycloak stores roles in realm_access.roles (realm-level) and resource_access.{client}.roles (client-level)
        roles.AddRange(ParseRolesFromRealmAccess(user));
        roles.AddRange(ParseRolesFromResourceAccess(user));

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
        foreach (string role in ParseRolesFromRealmAccessClaims(identity))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        foreach (string role in ParseRolesFromResourceAccessClaims(identity))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }

    private static List<string> ParseRolesFromJsonElement(JsonElement element)
    {
        List<string> roles = new List<string>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement role in element.EnumerateArray())
            {
                string? roleValue = role.GetString();
                if (!string.IsNullOrEmpty(roleValue))
                    roles.Add(roleValue);
            }
        }
        return roles;
    }

    private List<string> ParseRolesFromRealmAccess(ClaimsPrincipal user)
    {
        List<string> roles = new List<string>();
        string? realmAccess = user.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess)) return roles;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out JsonElement rolesElement))
            {
                roles.AddRange(ParseRolesFromJsonElement(rolesElement));
            }
        }
        catch { }
        return roles;
    }

    private List<string> ParseRolesFromResourceAccess(ClaimsPrincipal user)
    {
        List<string> roles = new List<string>();
        string? resourceAccess = user.FindFirst("resource_access")?.Value;
        if (string.IsNullOrEmpty(resourceAccess)) return roles;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resourceAccess);
            foreach (JsonProperty client in doc.RootElement.EnumerateObject())
            {
                if (client.Value.TryGetProperty("roles", out JsonElement clientRoles))
                {
                    roles.AddRange(ParseRolesFromJsonElement(clientRoles));
                }
            }
        }
        catch { }
        return roles;
    }

    private List<string> ParseRolesFromRealmAccessClaims(ClaimsIdentity identity)
    {
        List<string> roles = new List<string>();
        string? realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccess)) return roles;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out JsonElement rolesElement))
            {
                roles.AddRange(ParseRolesFromJsonElement(rolesElement));
            }
        }
        catch { }
        return roles;
    }

    private List<string> ParseRolesFromResourceAccessClaims(ClaimsIdentity identity)
    {
        List<string> roles = new List<string>();
        string? resourceAccess = identity.FindFirst("resource_access")?.Value;
        if (string.IsNullOrEmpty(resourceAccess)) return roles;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resourceAccess);
            foreach (JsonProperty client in doc.RootElement.EnumerateObject())
            {
                if (client.Value.TryGetProperty("roles", out JsonElement clientRoles))
                {
                    roles.AddRange(ParseRolesFromJsonElement(clientRoles));
                }
            }
        }
        catch { }
        return roles;
    }
}