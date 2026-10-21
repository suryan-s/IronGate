using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using IronGate.Domain;

namespace IronGate.Infrastructure.Redis;

/// <summary>
/// Redis-backed distributed rate limit store using sliding window algorithm via Lua script.
/// Atomic operations ensure consistency across multiple IronGate instances.
/// </summary>
public sealed class RedisRateLimitStore : IRateLimitStore, IDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<RedisRateLimitStore> _logger;
    private readonly RedisRateLimitOptions _options;

    // Lua script for atomic sliding window rate limiting
    // KEYS[1] = partition key (e.g., "public:192.168.1.1" or "protected:user:abc")
    // ARGV[1] = window size in milliseconds
    // ARGV[2] = permit limit
    // ARGV[3] = current timestamp in milliseconds
    // ARGV[4] = permit count to acquire
    private const string SlidingWindowScript = @"
        local key = KEYS[1]
        local window = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local count = tonumber(ARGV[4])
        local window_start = now - window
        
        -- Remove expired entries
        redis.call('ZREMRANGEBYSCORE', key, '-inf', window_start)
        
        -- Get current count
        local current = redis.call('ZCARD', key)
        
        if current + count <= limit then
            -- Add new entries with timestamp as score
            for i = 1, count do
                redis.call('ZADD', key, now, now .. '-' .. i .. '-' .. math.random(1000000))
            end
            -- Set expiry on key (window + buffer)
            redis.call('EXPIRE', key, math.ceil(window / 1000) + 1)
            return {1, limit - current - count}
        else
            -- Get oldest entry to calculate retry-after
            local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
            local retry_after = 0
            if #oldest > 0 then
                retry_after = math.ceil((tonumber(oldest[2]) + window - now) / 1000)
            end
            return {0, retry_after}
        end
    ";

    public RedisRateLimitStore(
        IConnectionMultiplexer redis,
        IOptions<RedisRateLimitOptions> options,
        ILogger<RedisRateLimitStore> logger)
    {
        _redis = (ConnectionMultiplexer)redis;
        _db = _redis.GetDatabase();
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<RateLimitLease> AcquireAsync(string partitionKey, int permitCount, CancellationToken cancellationToken = default)
    {
        var key = $"{_options.KeyPrefix}{partitionKey}";
        var windowMs = (long)_options.Window.TotalMilliseconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            var result = await _db.ScriptEvaluateAsync(
                SlidingWindowScript,
                new RedisKey[] { key },
                new RedisValue[] { windowMs, _options.PermitLimit, now, permitCount });

            var resultArray = (RedisResult[])result!;
            var acquired = (bool)resultArray[0];
            var metadata = (long)resultArray[1];

            if (acquired)
            {
                return new RedisRateLimitLease(
                    isAcquired: true,
                    retryAfter: TimeSpan.Zero,
                    currentAvailablePermits: metadata);
            }
            else
            {
                var retryAfter = TimeSpan.FromSeconds(metadata);
                return new RedisRateLimitLease(
                    isAcquired: false,
                    retryAfter: retryAfter,
                    currentAvailablePermits: 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis rate limit acquire failed for key {Key}", key);
            // Fail open - allow request through if Redis is down
            return new RedisRateLimitLease(
                isAcquired: true,
                retryAfter: TimeSpan.Zero,
                currentAvailablePermits: _options.PermitLimit);
        }
    }

    public async ValueTask<long> GetAvailablePermitsAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var key = $"{_options.KeyPrefix}{partitionKey}";
        var windowMs = (long)_options.Window.TotalMilliseconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowStart = now - windowMs;

        try
        {
            await _db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, windowStart);
            var current = await _db.SortedSetLengthAsync(key);
            return Math.Max(0, _options.PermitLimit - current);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis get available permits failed for key {Key}", key);
            return _options.PermitLimit;
        }
    }

    public async ValueTask ResetAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var key = $"{_options.KeyPrefix}{partitionKey}";
        try
        {
            await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis rate limit reset failed for key {Key}", key);
        }
    }

    public void Dispose()
    {
        // ConnectionMultiplexer is managed externally
    }
}

/// <summary>
/// Custom RateLimitLease implementation for Redis store.
/// </summary>
internal sealed class RedisRateLimitLease : RateLimitLease
{
    private readonly bool _isAcquired;
    private readonly TimeSpan _retryAfter;
    private readonly long _currentAvailablePermits;

    public RedisRateLimitLease(bool isAcquired, TimeSpan retryAfter, long currentAvailablePermits)
    {
        _isAcquired = isAcquired;
        _retryAfter = retryAfter;
        _currentAvailablePermits = currentAvailablePermits;
    }

    public override bool IsAcquired => _isAcquired;

    public override IReadOnlyList<string> MetadataNames => new[] { "RetryAfter", "CurrentAvailablePermits" };

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (metadataName == "RetryAfter")
        {
            metadata = _retryAfter;
            return true;
        }
        if (metadataName == "CurrentAvailablePermits")
        {
            metadata = _currentAvailablePermits;
            return true;
        }
        metadata = null;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        // No resources to dispose
    }
}

/// <summary>
/// Configuration options for Redis rate limit store.
/// </summary>
public sealed class RedisRateLimitOptions
{
    public const string SectionName = "RateLimiting:Redis";

    /// <summary>Whether Redis rate limiting is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Redis connection string.</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Redis key prefix for all rate limit keys.</summary>
    public string KeyPrefix { get; set; } = "irongate:ratelimit:";

    /// <summary>Sliding window size.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum permits per window.</summary>
    public long PermitLimit { get; set; } = 100;
}