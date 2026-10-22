using IronGate.Domain;
using IronGate.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace IronGate.Extensions;

public static class AdminApiExtensions
{
    public static IServiceCollection AddIronGateAdminApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AdminApiOptions>(
            configuration.GetSection(AdminApiOptions.SectionName));

        return services;
    }

    public static IApplicationBuilder UseIronGateAdminApi(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AdminApiMiddleware>();
    }
}

public sealed class AdminApiOptions
{
    public const string SectionName = "Admin";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class AdminApiMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AdminApiOptions _options;
    private readonly ILogger<AdminApiMiddleware> _logger;

    public AdminApiMiddleware(
        RequestDelegate next,
        IOptions<AdminApiOptions> options,
        ILogger<AdminApiMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only handle /admin/* paths
        if (!context.Request.Path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }

        // Check admin API key
        var providedKey = context.Request.Headers["X-IronGate-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(_options.ApiKey) || providedKey != _options.ApiKey)
        {
            _logger.LogWarning("Admin API access denied: invalid or missing API key");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/401",
                title = "Unauthorized",
                status = 401,
                detail = "Invalid or missing admin API key"
            }, context.RequestAborted);
            return;
        }

        // Handle admin endpoints
        var path = context.Request.Path.Value ?? "";
        
        if (path.Equals("/admin/status", StringComparison.OrdinalIgnoreCase) && 
            HttpMethods.IsGet(context.Request.Method))
        {
            await HandleStatusAsync(context);
            return;
        }
        
        if (path.Equals("/admin/cache", StringComparison.OrdinalIgnoreCase) && 
            HttpMethods.IsDelete(context.Request.Method))
        {
            await HandleClearCacheAsync(context);
            return;
        }
        
        if (path.StartsWith("/admin/ratelimit/", StringComparison.OrdinalIgnoreCase) && 
            HttpMethods.IsDelete(context.Request.Method))
        {
            await HandleResetRateLimitAsync(context);
            return;
        }

        // Not found
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await _next(context);
    }

    private async Task HandleStatusAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var config = services.GetRequiredService<IConfiguration>();
        
        // Get rate limit store for stats
        var rateLimitStore = services.GetService<IRateLimitStore>();
        
        // Get fallback cache store for stats
        var fallbackCacheStore = services.GetService<IFallbackCacheStore>();

        // Read feature flags from config dynamically
        var rateLimitingRedisEnabled = config.GetValue("RateLimiting:Redis:Enabled", false);
        var hedgingEnabled = config.GetValue("Resilience:Hedging:Enabled", false);
        var hedgingDelayMs = config.GetValue("Resilience:Hedging:DelayMs", 50);
        var hedgingMaxAttempts = config.GetValue("Resilience:Hedging:MaxHedgedAttempts", 2);
        var circuitBreakerEnabled = config.GetValue("Resilience:CircuitBreaker:Enabled", false);
        var circuitBreakerFailureRatio = config.GetValue("Resilience:CircuitBreaker:FailureRatio", 0.5);
        var circuitBreakerBreakDuration = config.GetValue("Resilience:CircuitBreaker:BreakDurationSeconds", 15);
        var fallbackCacheEnabled = config.GetValue("FallbackCache:Enabled", false);
        var fallbackCacheTtl = config.GetValue("FallbackCache:TtlSeconds", 300);
        var fallbackCacheUseRedis = config.GetValue("FallbackCache:UseRedis", false);
        var loadSheddingEnabled = config.GetValue("LoadShedding:Enabled", false);
        var loadSheddingMaxConcurrent = config.GetValue("LoadShedding:MaxConcurrentRequests", 500);
        var zeroTrustEnabled = !string.IsNullOrEmpty(config["DownstreamSecurity:SharedSecret"]);

        var response = new
        {
            timestamp = DateTimeOffset.UtcNow,
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            routes = new[]
            {
                new { path = "/api/public/*", policy = "Anonymous", rateLimit = "Sliding Window (10/min)" },
                new { path = "/api/protected/*", policy = "jwt-required", rateLimit = "Token Bucket (20 tokens, 5/10s)" }
            },
            clusters = new[]
            {
                new { id = "downstream-echo", loadBalancing = "PowerOfTwoChoices", destinations = new[] { "http://localhost:8081/" } }
            },
            features = new
            {
                redisRateLimiting = rateLimitingRedisEnabled,
                hedging = new { enabled = hedgingEnabled, delayMs = hedgingDelayMs, maxAttempts = hedgingMaxAttempts },
                circuitBreaker = new { enabled = circuitBreakerEnabled, failureRatio = circuitBreakerFailureRatio, breakDurationSeconds = circuitBreakerBreakDuration },
                fallbackCache = new { enabled = fallbackCacheEnabled, ttlSeconds = fallbackCacheTtl, useRedis = fallbackCacheUseRedis },
                loadShedding = new { enabled = loadSheddingEnabled, maxConcurrent = loadSheddingMaxConcurrent },
                zeroTrust = new { enabled = zeroTrustEnabled }
            },
            inFlightRequests = 0,
            uptime = DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(response);
    }

    private async Task HandleClearCacheAsync(HttpContext context)
    {
        var fallbackCacheStore = context.RequestServices.GetService<IFallbackCacheStore>();
        
        if (fallbackCacheStore == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/503",
                title = "Service Unavailable",
                status = 503,
                detail = "Fallback cache not configured"
            });
            return;
        }

        var path = context.Request.Query["path"].FirstOrDefault();
        
        if (!string.IsNullOrEmpty(path))
        {
            var cacheKey = $"irongate:fallback:{path}";
            await fallbackCacheStore.RemoveAsync(cacheKey, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new { message = $"Cache cleared for {path}", cacheKey });
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/501",
                title = "Not Implemented",
                status = 501,
                detail = "Cache clear all not implemented"
            });
        }
    }

    private async Task HandleResetRateLimitAsync(HttpContext context)
    {
        var rateLimitStore = context.RequestServices.GetService<IRateLimitStore>();
        
        if (rateLimitStore == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/503",
                title = "Service Unavailable",
                status = 503,
                detail = "Rate limit store not configured"
            });
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var key = path.TrimStart('/').Replace("/admin/ratelimit/", "");
        
        if (string.IsNullOrEmpty(key))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/400",
                title = "Bad Request",
                status = 400,
                detail = "Rate limit key required (e.g., /admin/ratelimit/public:192.168.1.1)"
            });
            return;
        }

        await rateLimitStore.RemoveAsync(key, context.RequestAborted);
        
        await context.Response.WriteAsJsonAsync(new { message = $"Rate limit reset for {key}" });
    }
}