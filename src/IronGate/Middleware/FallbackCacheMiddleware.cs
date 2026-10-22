using System.Text;
using IronGate.Domain;
using IronGate.Infrastructure.Caching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IronGate.Middleware;

public sealed class FallbackCacheMiddleware(
    RequestDelegate next,
    IOptions<FallbackCacheOptions> options,
    ILogger<FallbackCacheMiddleware> logger)
{
    private readonly FallbackCacheOptions _options = options.Value;
    private readonly ILogger<FallbackCacheMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        // Only apply fallback caching to GET requests
        if (!_options.Enabled || !HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Resolve the correct store at runtime based on current config
        var fallbackCacheOptions = context.RequestServices
            .GetRequiredService<IOptionsMonitor<FallbackCacheOptions>>().CurrentValue;
        
        IFallbackCacheStore cacheStore;
        if (fallbackCacheOptions.UseRedis)
        {
            cacheStore = context.RequestServices.GetRequiredService<RedisFallbackCacheStore>();
        }
        else
        {
            cacheStore = context.RequestServices.GetRequiredService<InMemoryFallbackCacheStore>();
        }

        var path = context.Request.Path.Value ?? "/";
        var query = context.Request.QueryString.Value ?? string.Empty;
        var cacheKey = $"irongate:fallback:{path}{query}";

        var originalBodyStream = context.Response.Body;
        using var bufferStream = new MemoryStream();
        context.Response.Body = bufferStream;

        try
        {
            await next(context);

            // 1. Happy Path: Cache 200 OK responses for future outages
            if (context.Response.StatusCode == StatusCodes.Status200OK)
            {
                var bodyBytes = bufferStream.ToArray();
                var contentType = context.Response.ContentType ?? "application/json";

                await cacheStore.SetAsync(
                    cacheKey,
                    new CachedResponseSnapshot(StatusCodes.Status200OK, contentType, bodyBytes, DateTimeOffset.UtcNow),
                    TimeSpan.FromSeconds(_options.TtlSeconds),
                    context.RequestAborted);

                bufferStream.Position = 0;
                await bufferStream.CopyToAsync(originalBodyStream, context.RequestAborted);
                return;
            }

            // 2. Outage Path (5xx from Downstream or 503 from Open Circuit Breaker)
            if (context.Response.StatusCode >= 500)
            {
                // Tier 1: Check Last-Known-Good Dynamic Cache
                var cached = await cacheStore.GetAsync(cacheKey, context.RequestAborted);
                if (cached is not null)
                {
                    _logger.LogWarning(
                        "Downstream returned {StatusCode} for {Path}. Serving cached fallback from {CachedAt}.",
                        context.Response.StatusCode, path, cached.CachedAtUtc);

                    await WriteFallbackResponseAsync(
                        context,
                        originalBodyStream,
                        cached.ContentType,
                        cached.Body,
                        source: "cache");
                    return;
                }

                // Tier 2: Check Static Fallback Config
                if (_options.StaticFallbacks.TryGetValue(path, out var staticPayload))
                {
                    _logger.LogWarning(
                        "Downstream returned {StatusCode} for {Path}. Serving configured static fallback.",
                        context.Response.StatusCode, path);

                    var staticBytes = Encoding.UTF8.GetBytes(staticPayload);
                    await WriteFallbackResponseAsync(
                        context,
                        originalBodyStream,
                        "application/json; charset=utf-8",
                        staticBytes,
                        source: "static-config");
                    return;
                }
            }

            // No fallback available; flush original response (e.g., 401, 404, 429, or un-cached 5xx)
            bufferStream.Position = 0;
            await bufferStream.CopyToAsync(originalBodyStream, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private static async Task WriteFallbackResponseAsync(
        HttpContext context,
        Stream originalBodyStream,
        string contentType,
        byte[] bodyBytes,
        string source)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bodyBytes.Length;
        context.Response.Headers["X-IronGate-Fallback"] = "true";
        context.Response.Headers["X-IronGate-Fallback-Source"] = source;

        await originalBodyStream.WriteAsync(bodyBytes, context.RequestAborted);
    }
}