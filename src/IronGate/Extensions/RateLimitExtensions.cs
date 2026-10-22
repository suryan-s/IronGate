using System.Globalization;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using IronGate.Domain;
using IronGate.Infrastructure.Redis;

namespace IronGate.Extensions;

public static class RateLimitExtensions
{
    public const string UnifiedPolicy = "default";

    public static IServiceCollection AddIronGateRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var publicConfig = configuration.GetSection("RateLimiting:Public");
        var protectedConfig = configuration.GetSection("RateLimiting:Protected");
        var redisConfig = configuration.GetSection("RateLimiting:Redis");

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

        // Register options for middleware
        services.AddSingleton(publicOptions);
        services.AddSingleton(protectedOptions);
        services.AddSingleton(unlimitedOptions);

        // Register rate limiter options for Redis store
        services.Configure<RedisRateLimitOptions>(redisConfig);

        // Register Redis connection (lazy - only connects when first used)
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisRateLimitOptions>>().Value;
            if (!options.Enabled)
            {
                return null!; // Will be handled by factory
            }
            var config = ConfigurationOptions.Parse(options.ConnectionString);
            config.AbortOnConnectFail = false;
            config.ConnectRetry = 3;
            config.ConnectTimeout = 5000;
            return ConnectionMultiplexer.Connect(config);
        });

        // Register BOTH stores - factory will pick the right one at runtime
        services.AddSingleton<InMemoryRateLimitStore>();
        services.AddSingleton<RedisRateLimitStore>();
        services.AddSingleton<IRateLimitStore>(sp => sp.GetRequiredService<InMemoryRateLimitStore>());

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
/// In-memory rate limit store (fallback when Redis is disabled).
/// Uses partitioned sliding window / token bucket limiters.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiterCache = new();
    private readonly SlidingWindowRateLimiterOptions _publicOptions;
    private readonly TokenBucketRateLimiterOptions _protectedOptions;
    private readonly FixedWindowRateLimiterOptions _unlimitedOptions;

    public InMemoryRateLimitStore(
        SlidingWindowRateLimiterOptions publicOptions,
        TokenBucketRateLimiterOptions protectedOptions,
        FixedWindowRateLimiterOptions unlimitedOptions)
    {
        _publicOptions = publicOptions;
        _protectedOptions = protectedOptions;
        _unlimitedOptions = unlimitedOptions;
    }

    public async ValueTask<RateLimitResult> AcquireAsync(string partitionKey, int permitCount, CancellationToken cancellationToken = default)
    {
        RateLimiter limiter;

        if (partitionKey.StartsWith("public:"))
        {
            limiter = _limiterCache.GetOrAdd(partitionKey, _ => new SlidingWindowRateLimiter(_publicOptions));
        }
        else if (partitionKey.StartsWith("protected:user:"))
        {
            limiter = _limiterCache.GetOrAdd(partitionKey, _ => new TokenBucketRateLimiter(_protectedOptions));
        }
        else
        {
            limiter = _limiterCache.GetOrAdd(partitionKey, _ => new FixedWindowRateLimiter(_unlimitedOptions));
        }

        var lease = await limiter.AcquireAsync(permitCount, cancellationToken);

        // Extract quota info from lease metadata
        var isAllowed = lease.IsAcquired;
        int limit, remaining;
        long resetEpoch;

        if (partitionKey.StartsWith("public:"))
        {
            limit = _publicOptions.PermitLimit;
            // For sliding window, we don't have easy access to remaining, estimate
            remaining = isAllowed ? limit - 1 : 0;
            resetEpoch = DateTimeOffset.UtcNow.Add(_publicOptions.Window).ToUnixTimeSeconds();
        }
        else if (partitionKey.StartsWith("protected:user:"))
        {
            limit = _protectedOptions.TokenLimit;
            // For token bucket, estimate remaining tokens
            remaining = isAllowed ? (int)_protectedOptions.TokenLimit - 1 : 0;
            resetEpoch = DateTimeOffset.UtcNow.Add(_protectedOptions.ReplenishmentPeriod).ToUnixTimeSeconds();
        }
        else
        {
            limit = int.MaxValue;
            remaining = int.MaxValue;
            resetEpoch = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeSeconds();
        }

        return new RateLimitResult(
            IsAllowed: isAllowed,
            Limit: limit,
            Remaining: Math.Max(0, remaining),
            ResetEpochSeconds: DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds(),
            Lease: isAllowed ? lease : null);
    }

    public ValueTask<long> GetAvailablePermitsAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        return new ValueTask<long>(0);
    }

    public ValueTask ResetAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        _limiterCache.TryRemove(partitionKey, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        _limiterCache.TryRemove(partitionKey, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Custom rate limiting middleware that handles partitioned rate limiting correctly.
/// Runs after authentication so JWT claims are available.
/// Supports both in-memory and distributed (Redis) rate limiting.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly IRateLimitStore _store;

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
        IRateLimitStore store,
        SlidingWindowRateLimiterOptions publicOptions,
        TokenBucketRateLimiterOptions protectedOptions,
        FixedWindowRateLimiterOptions unlimitedOptions)
    {
        _next = next;
        _logger = logger;
        _store = store;
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

        string partitionKey;
        TimeSpan fallbackRetryAfter;

        if (path.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase))
        {
            partitionKey = $"public:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip"}";
            fallbackRetryAfter = TimeSpan.FromSeconds(
                _publicOptions.Window.TotalSeconds / _publicOptions.SegmentsPerWindow);
        }
        else if (path.StartsWith("/api/protected/", StringComparison.OrdinalIgnoreCase))
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.User.FindFirstValue("sub")
                         ?? context.Connection.RemoteIpAddress?.ToString()
                         ?? "anonymous";
            partitionKey = $"protected:user:{userId}";
            fallbackRetryAfter = TimeSpan.FromSeconds(
                _protectedOptions.ReplenishmentPeriod.TotalSeconds / _protectedOptions.TokensPerPeriod);
        }
        else
        {
            await _next(context);
            return;
        }

        _logger.LogDebug("Rate limit check: path={Path}, partitionKey={PartitionKey}, store={Store}",
            path, partitionKey, _store.GetType().Name);

        // Try to acquire lease from distributed store
        var result = await _store.AcquireAsync(partitionKey, 1, context.RequestAborted);

        _logger.LogDebug("Rate limit result: partitionKey={PartitionKey}, acquired={Acquired}, limit={Limit}, remaining={Remaining}",
            partitionKey, result.IsAllowed, result.Limit, result.Remaining);

        // Add RFC-compliant rate limit headers to ALL responses
        context.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, result.Remaining).ToString();
        context.Response.Headers["X-RateLimit-Reset"] = result.ResetEpochSeconds.ToString();

        if (!result.IsAllowed)
        {
            // Rate limited - return 429
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";

            // Use limiter's metadata if available, otherwise fallback
            TimeSpan retryAfter = fallbackRetryAfter;
            if (result.Lease?.TryGetMetadata("RetryAfter", out var retryAfterObj) == true && retryAfterObj is TimeSpan ra)
            {
                retryAfter = ra;
            }
            else
            {
                retryAfter = fallbackRetryAfter;
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
            result.Lease?.Dispose();
        }
    }
}