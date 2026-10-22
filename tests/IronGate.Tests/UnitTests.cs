using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StackExchange.Redis;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Xunit;
using IronGate.Middleware;
using IronGate.Domain;
using IronGate.Infrastructure.Caching;
using IronGate.Infrastructure.Keycloak;
using IronGate.Infrastructure.Resilience;
using IronGate.Extensions;
using IronGate.Infrastructure.Redis;
using Yarp.ReverseProxy.Forwarder;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

namespace IronGate.Tests;

public class UnitTests
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    #region CorrelationIdMiddleware Tests

    [Fact]
    public async Task CorrelationIdMiddleware_AddsCorrelationId_WhenMissing()
    {
        var logger = new Mock<ILogger<CorrelationIdMiddleware>>();
        var middleware = new CorrelationIdMiddleware(
            async (ctx) => await Task.CompletedTask,
            logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Correlation-Id"] = "";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        var headerValue = context.Response.Headers["X-Correlation-Id"].ToString();
        Assert.False(string.IsNullOrEmpty(headerValue));
    }

    [Fact]
    public async Task CorrelationIdMiddleware_PreservesExistingCorrelationId()
    {
        var logger = new Mock<ILogger<CorrelationIdMiddleware>>();
        var middleware = new CorrelationIdMiddleware(
            async (ctx) => await Task.CompletedTask,
            logger.Object);

        var context = new DefaultHttpContext();
        var existingId = "existing-correlation-123";
        context.Request.Headers["X-Correlation-Id"] = existingId;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(existingId, context.Response.Headers["X-Correlation-Id"].FirstOrDefault());
    }

    #endregion

    #region LoadSheddingMiddleware Tests

    [Fact]
    public async Task LoadSheddingMiddleware_AllowsRequest_WhenUnderLimit()
    {
        var options = new LoadSheddingOptions { Enabled = true, MaxConcurrentRequests = 10 };
        var logger = new Mock<ILogger<LoadSheddingMiddleware>>();
        var meter = new Meter("Test");
        
        var middleware = new LoadSheddingMiddleware(
            async (ctx) => await Task.CompletedTask,
            Mock.Of<ILogger<LoadSheddingMiddleware>>(),
            Options.Create(options),
            meter);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task LoadSheddingMiddleware_AllowsPriorityRequest()
    {
        var options = new LoadSheddingOptions { Enabled = true, MaxConcurrentRequests = 1 };
        var logger = new Mock<ILogger<LoadSheddingMiddleware>>();
        var meter = new Meter("Test");
        
        var middleware = new LoadSheddingMiddleware(
            async (ctx) => await Task.CompletedTask,
            Mock.Of<ILogger<LoadSheddingMiddleware>>(),
            Options.Create(options),
            meter);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-IronGate-Priority"] = "high";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    #endregion

    #region RateLimitMiddleware Tests

    [Fact]
    public async Task RateLimitMiddleware_AddsRateLimitHeaders()
    {
        var publicOptions = new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        };

        var protectedOptions = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true,
            QueueLimit = 0
        };

        var unlimitedOptions = new FixedWindowRateLimiterOptions
        {
            PermitLimit = int.MaxValue,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        };

        var services = new ServiceCollection();
        services.AddSingleton(publicOptions);
        services.AddSingleton(protectedOptions);
        services.AddSingleton(unlimitedOptions);
        services.AddSingleton<InMemoryRateLimitStore>(sp => new InMemoryRateLimitStore(publicOptions, protectedOptions, unlimitedOptions));
        services.AddSingleton<IRateLimitStore>(sp => sp.GetRequiredService<InMemoryRateLimitStore>());
        services.AddLogging();
        
        var provider = services.BuildServiceProvider();
        var middleware = new RateLimitMiddleware(
            async (ctx) => await Task.CompletedTask,
            Mock.Of<ILogger<RateLimitMiddleware>>(),
            provider,
            publicOptions,
            protectedOptions,
            unlimitedOptions);

        // Test public route
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/public/test";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.1");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(context.Response.Headers.ContainsKey("X-RateLimit-Limit"));
        Assert.True(context.Response.Headers.ContainsKey("X-RateLimit-Remaining"));
        Assert.True(context.Response.Headers.ContainsKey("X-RateLimit-Reset"));
    }

    #endregion

    #region InMemoryRateLimitStore Tests

    [Fact]
    public async Task InMemoryRateLimitStore_AcquireAsync_ReturnsAllowed()
    {
        var publicOptions = new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        };

        var protectedOptions = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true,
            QueueLimit = 0
        };

        var unlimitedOptions = new FixedWindowRateLimiterOptions
        {
            PermitLimit = int.MaxValue,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        };

        var store = new InMemoryRateLimitStore(publicOptions, protectedOptions, unlimitedOptions);

        var result = await store.AcquireAsync("public:192.168.1.1", 1);

        Assert.True(result.IsAllowed);
        Assert.Equal(10, result.Limit);
        Assert.True(result.Remaining >= 0);
    }

    [Fact]
    public async Task InMemoryRateLimitStore_AcquireAsync_ResetsCorrectly()
    {
        var publicOptions = new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        };

        var protectedOptions = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            AutoReplenishment = true,
            QueueLimit = 0
        };

        var unlimitedOptions = new FixedWindowRateLimiterOptions
        {
            PermitLimit = int.MaxValue,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        };

        var store = new InMemoryRateLimitStore(publicOptions, protectedOptions, unlimitedOptions);

        await store.AcquireAsync("public:192.168.1.1", 1);
        await store.ResetAsync("public:192.168.1.1");

        var result = await store.AcquireAsync("public:192.168.1.1", 1);
        Assert.True(result.IsAllowed);
    }

    #endregion

    #region InMemoryFallbackCacheStore Tests

    [Fact]
    public async Task InMemoryFallbackCacheStore_GetSet_Works()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new InMemoryFallbackCacheStore(memoryCache);

        var snapshot = new CachedResponseSnapshot(
            StatusCodes.Status200OK,
            "application/json",
            System.Text.Encoding.UTF8.GetBytes("{\"test\":\"data\"}"),
            DateTimeOffset.UtcNow);

        await store.SetAsync("test-key", snapshot, TimeSpan.FromSeconds(60));
        var result = await store.GetAsync("test-key");

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
    }

    [Fact]
    public async Task InMemoryFallbackCacheStore_RemoveAsync_Works()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new InMemoryFallbackCacheStore(memoryCache);

        var snapshot = new CachedResponseSnapshot(
            StatusCodes.Status200OK,
            "application/json",
            System.Text.Encoding.UTF8.GetBytes("{\"test\":\"data\"}"),
            DateTimeOffset.UtcNow);

        await store.SetAsync("test-key", snapshot, TimeSpan.FromSeconds(60));
        await store.RemoveAsync("test-key");
        
        var result = await store.GetAsync("test-key");
        Assert.Null(result);
    }

    #endregion

    #region KeycloakIdentityProvider Tests

    [Fact]
    public void KeycloakIdentityProvider_GetUserId_ReturnsSub()
    {
        var provider = new KeycloakIdentityProvider();
        var claims = new List<Claim>
        {
            new Claim("sub", "user-123"),
            new Claim("preferred_username", "testuser")
        };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var userId = provider.GetUserId(principal);

        Assert.Equal("user-123", userId);
    }

    [Fact]
    public void KeycloakIdentityProvider_GetUsername_ReturnsPreferredUsername()
    {
        var provider = new KeycloakIdentityProvider();
        var claims = new List<Claim>
        {
            new Claim("sub", "user-123"),
            new Claim("preferred_username", "testuser")
        };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var username = provider.GetUsername(principal);

        Assert.Equal("testuser", username);
    }

    [Fact]
    public void KeycloakIdentityProvider_GetRoles_ExtractsFromRealmAccess()
    {
        var provider = new KeycloakIdentityProvider();
        var realmAccess = JsonSerializer.Serialize(new { roles = new[] { "admin", "user" } });
        var claims = new List<Claim>
        {
            new Claim("sub", "user-123"),
            new Claim("realm_access", realmAccess)
        };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var roles = provider.GetRoles(principal);

        Assert.Contains("admin", roles);
        Assert.Contains("user", roles);
    }

    [Fact]
    public void KeycloakIdentityProvider_MapClaims_AddsRoleClaims()
    {
        var provider = new KeycloakIdentityProvider();
        var realmAccess = JsonSerializer.Serialize(new { roles = new[] { "admin", "user" } });
        var claims = new List<Claim>
        {
            new Claim("sub", "user-123"),
            new Claim("realm_access", realmAccess)
        };
        var identity = new ClaimsIdentity(claims);

        provider.MapClaims(identity);

        Assert.Contains(identity.Claims, c => c.Type == ClaimTypes.Role && c.Value == "admin");
        Assert.Contains(identity.Claims, c => c.Type == ClaimTypes.Role && c.Value == "user");
    }

    #endregion

    #region AdminApiMiddleware Tests

    [Fact]
    public async Task AdminApiMiddleware_Returns401_WhenNoApiKey()
    {
        var options = new AdminApiOptions { ApiKey = "secret-key" };
        var logger = Mock.Of<ILogger<AdminApiMiddleware>>();
        var middleware = new AdminApiMiddleware(
            async (ctx) => await Task.CompletedTask,
            Options.Create(options),
            Mock.Of<ILogger<AdminApiMiddleware>>());

        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/status";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminApiMiddleware_Returns401_WhenWrongApiKey()
    {
        var options = new AdminApiOptions { ApiKey = "secret-key" };
        var logger = Mock.Of<ILogger<AdminApiMiddleware>>();
        var middleware = new AdminApiMiddleware(
            async (ctx) => await Task.CompletedTask,
            Options.Create(options),
            Mock.Of<ILogger<AdminApiMiddleware>>());

        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/status";
        context.Request.Headers["X-IronGate-Admin-Key"] = "wrong-key";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminApiMiddleware_Returns404_ForUnknownPath()
    {
        var options = new AdminApiOptions { ApiKey = "secret-key" };
        var logger = Mock.Of<ILogger<AdminApiMiddleware>>();
        var middleware = new AdminApiMiddleware(
            async (ctx) => await Task.CompletedTask,
            Options.Create(options),
            Mock.Of<ILogger<AdminApiMiddleware>>());

        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/unknown";
        context.Request.Headers["X-IronGate-Admin-Key"] = "secret-key";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    #endregion

    #region Options Tests

    [Fact]
    public void AdminApiOptions_Defaults()
    {
        var options = new AdminApiOptions();
        Assert.Equal("Admin", AdminApiOptions.SectionName);
        Assert.Equal(string.Empty, options.ApiKey);
    }

    [Fact]
    public void LoadSheddingOptions_Defaults()
    {
        var options = new LoadSheddingOptions();
        Assert.Equal("LoadShedding", LoadSheddingOptions.SectionName);
        Assert.True(options.Enabled);
        Assert.Equal(500, options.MaxConcurrentRequests);
        Assert.Equal(0, options.QueueLimit);
    }

    [Fact]
    public void ResilienceOptions_Defaults()
    {
        var options = new ResilienceOptions();
        Assert.Equal("Resilience", ResilienceOptions.SectionName);
        Assert.True(options.Enabled);
        Assert.Equal(10, options.TotalRequestTimeoutSeconds);
        Assert.NotNull(options.Hedging);
        Assert.NotNull(options.CircuitBreaker);
    }

    [Fact]
    public void HedgingConfig_Defaults()
    {
        var config = new HedgingConfig();
        Assert.True(config.Enabled);
        Assert.Equal(2, config.MaxHedgedAttempts);
        Assert.Equal(150, config.DelayMs);
    }

    [Fact]
    public void CircuitBreakerConfig_Defaults()
    {
        var config = new CircuitBreakerConfig();
        Assert.True(config.Enabled);
        Assert.Equal(0.5, config.FailureRatio);
        Assert.Equal(5, config.MinimumThroughput);
        Assert.Equal(10, config.SamplingDurationSeconds);
        Assert.Equal(15, config.BreakDurationSeconds);
    }

    [Fact]
    public void FallbackCacheOptions_Defaults()
    {
        var options = new FallbackCacheOptions();
        Assert.Equal("FallbackCache", FallbackCacheOptions.SectionName);
        Assert.True(options.Enabled);
        Assert.Equal(300, options.TtlSeconds);
        Assert.False(options.UseRedis);
        Assert.NotNull(options.StaticFallbacks);
    }

    [Fact]
    public void RedisRateLimitOptions_Defaults()
    {
        var options = new RedisRateLimitOptions();
        Assert.Equal("RateLimiting:Redis", RedisRateLimitOptions.SectionName);
        Assert.False(options.Enabled);
        Assert.Equal("localhost:6379", options.ConnectionString);
        Assert.Equal("irongate:ratelimit:", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Window);
        Assert.Equal(100L, options.PermitLimit);
    }

    [Fact]
    public void RedisFallbackCacheOptions_Defaults()
    {
        var options = new RedisFallbackCacheOptions();
        Assert.Equal("FallbackCache:Redis", RedisFallbackCacheOptions.SectionName);
        Assert.Equal("localhost:6379", options.ConnectionString);
    }

    [Fact]
    public void RateLimitResult_ConstructsCorrectly()
    {
        var result = new RateLimitResult(
            IsAllowed: true,
            Limit: 10,
            Remaining: 9,
            ResetEpochSeconds: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());

        Assert.True(result.IsAllowed);
        Assert.Equal(10, result.Limit);
        Assert.Equal(9, result.Remaining);
        Assert.True(result.ResetEpochSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public void RateLimitResult_Denied_ZeroRemaining()
    {
        var result = new RateLimitResult(
            IsAllowed: false,
            Limit: 10,
            Remaining: 0,
            ResetEpochSeconds: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());

        Assert.False(result.IsAllowed);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public void CachedResponseSnapshot_ConstructsCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CachedResponseSnapshot(
            StatusCodes.Status200OK,
            "application/json",
            System.Text.Encoding.UTF8.GetBytes("{}"),
            now);

        Assert.Equal(StatusCodes.Status200OK, snapshot.StatusCode);
        Assert.Equal("application/json", snapshot.ContentType);
        Assert.Equal(now, snapshot.CachedAtUtc);
    }

    [Fact]
    public void RateLimitResult_Equality()
    {
        var r1 = new RateLimitResult(true, 10, 5, 1000);
        var r2 = new RateLimitResult(true, 10, 5, 1000);
        var r3 = new RateLimitResult(false, 10, 0, 1000);

        Assert.Equal(r1, r2);
        Assert.NotEqual(r1, r3);
    }

    [Fact]
    public void CachedResponseSnapshot_Equality()
    {
        var now = DateTimeOffset.UtcNow;
        var s1 = new CachedResponseSnapshot(200, "json", new byte[] { 1, 2 }, now);
        var s2 = new CachedResponseSnapshot(200, "json", new byte[] { 1, 2 }, now);
        var s3 = new CachedResponseSnapshot(404, "json", new byte[] { 1, 2 }, now);

        // Compare individual properties since record equality doesn't compare byte arrays by value
        Assert.Equal(s1.StatusCode, s2.StatusCode);
        Assert.Equal(s1.ContentType, s2.ContentType);
        Assert.Equal(s1.Body, s2.Body);
        Assert.Equal(s1.CachedAtUtc, s2.CachedAtUtc);
        
        Assert.NotEqual(s1.StatusCode, s3.StatusCode);
    }

    #endregion
}