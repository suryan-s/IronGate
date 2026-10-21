using IronGate.Extensions;
using IronGate.Middleware;
using Yarp.ReverseProxy.Configuration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddIronGateAuth(builder.Configuration)
    .AddIronGateRateLimiting(builder.Configuration)
    .AddIronGateReverseProxy(builder.Configuration)
    .AddIronGateResilience(builder.Configuration)
    .AddIronGateFallbackCache(builder.Configuration)
    .AddIronGateOtel(builder.Configuration);

WebApplication app = builder.Build();

// Correlation ID must be first to capture all requests
app.UseCorrelationId();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "IronGate" }));

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint();

app.UseAuthentication();
app.UseAuthorization();

// Custom rate limiting middleware (runs after auth so JWT claims are available)
app.UseMiddleware<RateLimitMiddleware>();

// Fallback cache middleware - intercepts 5xx from YARP/CircuitBreaker, serves cached fallback
app.UseIronGateFallbackCache();

app.MapReverseProxy();

app.Run();

public partial class Program;