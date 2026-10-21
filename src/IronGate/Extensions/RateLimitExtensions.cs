using System.Globalization;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IronGate.Extensions;

public static class RateLimitExtensions
{
    public const string UnifiedPolicy = "default";

    // Cache limiters by partition key
    private static readonly ConcurrentDictionary<string, RateLimiter> _limiterCache = new();

    public static IServiceCollection AddIronGateRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var publicConfig = configuration.GetSection("RateLimiting:Public");
        var protectedConfig = configuration.GetSection("RateLimiting:Protected");

        // Pre-create limiter options for reuse
        var publicOptions = new SlidingWindowRateLimiterOptions
        {
            PermitLimit = publicConfig.GetValue("PermitLimit", 10),
            Window = TimeSpan.FromSeconds(publicConfig.GetValue("WindowSeconds", 60)),
            SegmentsPerWindow = publicConfig.GetValue("SegmentsPerWindow", 6),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        };

        var protectedOptions = new TokenBucketRateLimiterOptions
        {
            TokenLimit = protectedConfig.GetValue("TokenLimit", 20),
            TokensPerPeriod = protectedConfig.GetValue("TokensPerPeriod", 5),
            ReplenishmentPeriod = TimeSpan.FromSeconds(
                protectedConfig.GetValue("ReplenishmentPeriodSeconds", 10)),
            AutoReplenishment = true,
            QueueLimit = 0
        };

        var unlimitedOptions = new FixedWindowRateLimiterOptions
        {
            PermitLimit = int.MaxValue,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        };

        // Store options for middleware access
        services.AddSingleton(publicOptions);
        services.AddSingleton(protectedOptions);
        services.AddSingleton(unlimitedOptions);

        // Register the rate limiter service for DI
        services.AddSingleton<RateLimiter>(_ => new FixedWindowRateLimiter(unlimitedOptions));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata("RetryAfter", out var retryAfterObj) && retryAfterObj is TimeSpan retryAfter)
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                var correlationId = context.HttpContext.Response.Headers["x-correlation-id"].ToString();

                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://httpstatuses.com/429",
                    title = "Too Many Requests",
                    status = 429,
                    detail = "Rate limit quota exceeded. Please slow down and try again later.",
                    correlationId = string.IsNullOrEmpty(correlationId) ? null : correlationId
                }, cancellationToken);
            };

            // Dummy policy - actual rate limiting done in middleware
            options.AddPolicy<string>(UnifiedPolicy, _ => RateLimitPartition.GetFixedWindowLimiter(
                "dummy", _ => unlimitedOptions));
        });

        return services;
    }
}

/// <summary>
/// Custom rate limiting middleware that handles partitioned rate limiting correctly.
/// Runs after authentication so JWT claims are available.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly ConcurrentDictionary<string, RateLimiter> _limiterCache = new();

    // Limiter options (injected)
    private readonly SlidingWindowRateLimiterOptions _publicOptions;
    private readonly TokenBucketRateLimiterOptions _protectedOptions;
    private readonly FixedWindowRateLimiterOptions _unlimitedOptions;

    // Fallback retry-after values (used when limiter doesn't provide metadata)
    private readonly TimeSpan _publicRetryAfter;
    private readonly TimeSpan _protectedRetryAfter;

    public RateLimitMiddleware(
        RequestDelegate next,
        ILogger<RateLimitMiddleware> logger,
        SlidingWindowRateLimiterOptions publicOptions,
        TokenBucketRateLimiterOptions protectedOptions,
        FixedWindowRateLimiterOptions unlimitedOptions)
    {
        _next = next;
        _logger = logger;
        _publicOptions = publicOptions;
        _protectedOptions = protectedOptions;
        _unlimitedOptions = unlimitedOptions;

        // Fallback retry-after: time per permit/token
        _publicRetryAfter = TimeSpan.FromSeconds(
            publicOptions.Window.TotalSeconds / publicOptions.SegmentsPerWindow);
        _protectedRetryAfter = TimeSpan.FromSeconds(
            protectedOptions.ReplenishmentPeriod.TotalSeconds / protectedOptions.TokensPerPeriod);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip rate limiting for healthz and metrics
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        RateLimiter limiter;
        TimeSpan fallbackRetryAfter;

        if (path.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase))
        {
            var partitionKey = $"public:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip"}";
            limiter = _limiterCache.GetOrAdd(partitionKey, _ => new SlidingWindowRateLimiter(_publicOptions));
            fallbackRetryAfter = TimeSpan.FromSeconds(
                _publicOptions.Window.TotalSeconds / _publicOptions.SegmentsPerWindow);
        }
        else if (path.StartsWith("/api/protected/", StringComparison.OrdinalIgnoreCase))
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.User.FindFirstValue("sub")
                         ?? context.Connection.RemoteIpAddress?.ToString()
                         ?? "anonymous";
            var partitionKey = $"protected:user:{userId}";
            limiter = _limiterCache.GetOrAdd(partitionKey, _ => new TokenBucketRateLimiter(_protectedOptions));
            fallbackRetryAfter = TimeSpan.FromSeconds(
                _protectedOptions.ReplenishmentPeriod.TotalSeconds / _protectedOptions.TokensPerPeriod);
        }
        else
        {
            await _next(context);
            return;
        }

        // Try to acquire lease
        var lease = await limiter.AcquireAsync(1, context.RequestAborted);

        if (!lease.IsAcquired)
        {
            // Rate limited - return 429
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";

            // Use limiter's metadata if available, otherwise fallback
            TimeSpan retryAfter = fallbackRetryAfter;
            if (lease.TryGetMetadata("RetryAfter", out var retryAfterObj) && retryAfterObj is TimeSpan ra)
            {
                retryAfter = ra;
            }

            context.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);

            var correlationId = context.Response.Headers["x-correlation-id"].ToString();

            var problemDetails = new
            {
                type = "https://httpstatuses.com/429",
                title = "Too Many Requests",
                status = 429,
                detail = "Rate limit quota exceeded. Please slow down and try again later.",
                correlationId = string.IsNullOrEmpty(correlationId) ? null : correlationId
            };

            // Serialize manually to avoid WriteAsJsonAsync overwriting content type
            var json = System.Text.Json.JsonSerializer.Serialize(problemDetails);
            await context.Response.WriteAsync(json, context.RequestAborted);
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            lease.Dispose();
        }
    }
}