using System.Text.Json;
using IronGate.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IronGate.Infrastructure.Caching;

/// <summary>
/// Redis-backed fallback cache store with L1 (in-memory) + L2 (Redis) hybrid architecture.
/// L1: Short TTL, fast access. L2: Full TTL, distributed across instances.
/// </summary>
public sealed class RedisFallbackCacheStore : IFallbackCacheStore, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<RedisFallbackCacheStore> _logger;
    private readonly FallbackCacheOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public RedisFallbackCacheStore(
        IMemoryCache memoryCache,
        IConnectionMultiplexer redis,
        IOptions<FallbackCacheOptions> options,
        ILogger<RedisFallbackCacheStore> logger)
    {
        _memoryCache = memoryCache;
        _redis = redis;
        _db = redis.GetDatabase();
        _options = options.Value;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async ValueTask<CachedResponseSnapshot?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var fullKey = $"irongate:fallback:{cacheKey}";

        // L1: Check in-memory cache first
        if (_memoryCache.TryGetValue(fullKey, out CachedResponseSnapshot? l1Snapshot))
        {
            _logger.LogDebug("Fallback cache HIT (L1): {Key}", cacheKey);
            return l1Snapshot;
        }

        // L2: Check Redis
        try
        {
            var redisValue = await _db.StringGetAsync(fullKey);
            if (redisValue.HasValue)
            {
                var json = redisValue.ToString();
                var snapshot = JsonSerializer.Deserialize<CachedResponseSnapshot>(json, _jsonOptions);
                if (snapshot != null)
                {
                    // Populate L1 cache with shorter TTL (e.g., 30s)
                    _memoryCache.Set(fullKey, snapshot, TimeSpan.FromSeconds(30));
                    _logger.LogDebug("Fallback cache HIT (L2→L1): {Key}", cacheKey);
                    return snapshot;
                }
            }
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed, falling back to L1 only: {Key}", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis fallback cache get failed: {Key}", cacheKey);
        }

        _logger.LogDebug("Fallback cache MISS: {Key}", cacheKey);
        return null;
    }

    public async ValueTask SetAsync(string cacheKey, CachedResponseSnapshot snapshot, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var fullKey = $"irongate:fallback:{cacheKey}";

        // L1: Set in memory with short TTL (30s)
        _memoryCache.Set(fullKey, snapshot, TimeSpan.FromSeconds(30));

        // L2: Set in Redis with full TTL
        try
        {
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            await _db.StringSetAsync(fullKey, json, ttl);
            _logger.LogDebug("Fallback cache SET (L1+L2): {Key}, TTL={TTL}s", cacheKey, ttl.TotalSeconds);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed during SET, L1 cache still set: {Key}", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis fallback cache set failed: {Key}", cacheKey);
        }
    }

    public async ValueTask RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var fullKey = $"irongate:fallback:{cacheKey}";
        
        // Remove from L1
        _memoryCache.Remove(fullKey);

        // Remove from L2
        try
        {
            await _db.KeyDeleteAsync(fullKey);
            _logger.LogDebug("Fallback cache REMOVE (L1+L2): {Key}", cacheKey);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex, "Redis connection failed during REMOVE, L1 still removed: {Key}", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis fallback cache remove failed: {Key}", cacheKey);
        }
    }

    public void Dispose()
    {
        // ConnectionMultiplexer is managed externally
    }
}