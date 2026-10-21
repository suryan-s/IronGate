using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace IronGate.AspNetCore;

/// <summary>
/// Extension methods for IronGate downstream authentication.
/// </summary>
public static class IronGateDownstreamAuthExtensions
{
    /// <summary>
    /// Adds IronGate downstream authentication to the service collection.
    /// This configures authentication to trust X-IronGate-* headers forwarded by the IronGate gateway.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronGateDownstreamAuth(
        this IServiceCollection services,
        Action<IronGateDownstreamOptions>? configureOptions = null)
    {
        var options = new IronGateDownstreamOptions();
        configureOptions?.Invoke(options);

        services.AddSingleton(options);
        services.AddAuthentication(IronGateDownstreamDefaults.AuthenticationScheme)
            .AddScheme<IronGateDownstreamOptions, IronGateDownstreamAuthHandler>(
                IronGateDownstreamDefaults.AuthenticationScheme, null);

        return services;
    }

    /// <summary>
    /// Adds IronGate downstream authentication with default options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronGateDownstreamAuth(this IServiceCollection services)
    {
        return services.AddIronGateDownstreamAuth();
    }
}

/// <summary>
/// Default authentication scheme for IronGate downstream auth.
/// </summary>
public static class IronGateDownstreamDefaults
{
    public const string AuthenticationScheme = "IronGateDownstream";
}

/// <summary>
/// Configuration options for IronGate downstream authentication.
/// </summary>
public sealed class IronGateDownstreamOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Header name for the user ID forwarded by IronGate.
    /// </summary>
    public string UserIdHeader { get; set; } = "X-IronGate-User-Id";

    /// <summary>
    /// Header name for the username forwarded by IronGate.
    /// </summary>
    public string UsernameHeader { get; set; } = "X-IronGate-Username";

    /// <summary>
    /// Header name for the roles forwarded by IronGate.
    /// </summary>
    public string RolesHeader { get; set; } = "X-IronGate-Roles";

    /// <summary>
    /// Header name for the correlation ID forwarded by IronGate.
    /// </summary>
    public string CorrelationIdHeader { get; set; } = "X-Correlation-Id";

    /// <summary>
    /// Whether to require the headers to be present (fail auth if missing).
    /// Set to false for optional auth (e.g., public endpoints).
    /// </summary>
    public bool RequireHeaders { get; set; } = true;

    /// <summary>
    /// Role claims separator (e.g., comma-separated roles).
    /// </summary>
    public char RolesSeparator { get; set; } = ',';
}

/// <summary>
/// Authentication handler that reads X-IronGate-* headers and creates a ClaimsPrincipal.
/// </summary>
public sealed class IronGateDownstreamAuthHandler : AuthenticationHandler<IronGateDownstreamOptions>
{
    public IronGateDownstreamAuthHandler(
        IOptionsMonitor<IronGateDownstreamOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[Options.UserIdHeader].FirstOrDefault();
        var username = Request.Headers[Options.UsernameHeader].FirstOrDefault();
        var rolesHeader = Request.Headers[Options.RolesHeader].FirstOrDefault();
        var correlationId = Request.Headers[Options.CorrelationIdHeader].FirstOrDefault();

        // If required headers are missing and required, fail
        if (Options.RequireHeaders && string.IsNullOrEmpty(userId))
        {
            return AuthenticateResult.Fail($"Missing required header: {Options.UserIdHeader}");
        }

        // If no headers and not required, skip (allow anonymous)
        if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(username) && string.IsNullOrEmpty(rolesHeader))
        {
            return AuthenticateResult.NoResult();
        }

        // Build claims
        var claims = new List<Claim>();

        if (!string.IsNullOrEmpty(userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }

        if (!string.IsNullOrEmpty(username))
        {
            claims.Add(new Claim(ClaimTypes.Name, username));
        }

        if (!string.IsNullOrEmpty(rolesHeader))
        {
            foreach (var role in rolesHeader.Split(Options.RolesSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            claims.Add(new Claim("correlation_id", correlationId));
        }

        var identity = new ClaimsIdentity(claims, IronGateDownstreamDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, IronGateDownstreamDefaults.AuthenticationScheme);

        return AuthenticateResult.Success(ticket);
    }
}

/// <summary>
/// Middleware to ensure correlation ID is available in HttpContext.
/// </summary>
public sealed class IronGateCorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public IronGateCorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(correlationId))
        {
            context.Items["CorrelationId"] = correlationId;
        }
        await _next(context);
    }
}

/// <summary>
/// Extension methods for the correlation ID middleware.
/// </summary>
public static class IronGateCorrelationIdExtensions
{
    /// <summary>
    /// Adds the IronGate correlation ID middleware.
    /// </summary>
    public static IApplicationBuilder UseIronGateCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<IronGateCorrelationIdMiddleware>();
    }
}