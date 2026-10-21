using IronGate.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace IronGate.Infrastructure.Caching;

public sealed class InMemoryFallbackCacheStore(IMemoryCache memoryCache) : IFallbackCacheStore
{
    public ValueTask<CachedResponseSnapshot?> GetAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        memoryCache.TryGetValue(cacheKey, out CachedResponseSnapshot? snapshot);
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask SetAsync(
        string cacheKey,
        CachedResponseSnapshot snapshot,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        memoryCache.Set(cacheKey, snapshot, ttl);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        memoryCache.Remove(cacheKey);
        return ValueTask.CompletedTask;
    }
}