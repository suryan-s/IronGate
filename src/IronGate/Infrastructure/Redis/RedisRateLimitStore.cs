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
public sealed class RedisRateLimitStore : IRateLimitStore
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
    // Returns: {acquired (0/1), remaining, reset_epoch_seconds, retry_after_seconds}
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
            local remaining = limit - current - count
            local reset_epoch = math.ceil((now + window) / 1000)
            return {1, remaining, reset_epoch, 0}
        else
            -- Get oldest entry to calculate retry-after
            local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
            local retry_after = 0
            if #oldest > 0 then
                retry_after = math.ceil((tonumber(oldest[2]) + window - now) / 1000)
            end
            local reset_epoch = math.ceil((now + window) / 1000)
            return {0, 0, reset_epoch, retry_after}
        end
    ";

    public RedisRateLimitStore(
        IConnectionMultiplexer redis,
        IOptions<RedisRateLimitOptions> options,
        ILogger<RedisRateLimitStore> logger)
    {
        _redis = (ConnectionMultiplexer)redis;
        _db = redis.GetDatabase();
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<RateLimitResult> AcquireAsync(string partitionKey, int permitCount, CancellationToken cancellationToken = default)
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
            var remaining = (long)resultArray[1];
            var resetEpoch = (long)resultArray[2];
            var retryAfter = (long)resultArray[3];

            var limit = _options.PermitLimit;

            if (acquired)
            {
                return new RateLimitResult(
                    IsAllowed: true,
                    Limit: (int)limit,
                    Remaining: (int)Math.Max(0, remaining),
                    ResetEpochSeconds: resetEpoch);
            }
            else
            {
                return new RateLimitResult(
                    IsAllowed: false,
                    Limit: (int)limit,
                    Remaining: 0,
                    ResetEpochSeconds: resetEpoch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis rate limit acquire failed for key {Key}", key);
            // Fail open - allow request through if Redis is down
            return new RateLimitResult(
                IsAllowed: true,
                Limit: (int)_options.PermitLimit,
                Remaining: (int)_options.PermitLimit,
                ResetEpochSeconds: DateTimeOffset.UtcNow.Add(_options.Window).ToUnixTimeSeconds());
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

    public async ValueTask RemoveAsync(string partitionKey, CancellationToken cancellationToken = default)
    {
        var key = $"{_options.KeyPrefix}{partitionKey}";
        try
        {
            await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis rate limit remove failed for key {Key}", key);
        }
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