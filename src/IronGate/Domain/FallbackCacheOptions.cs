namespace IronGate.Domain;

public sealed record CachedResponseSnapshot(
    int StatusCode,
    string ContentType,
    byte[] Body,
    DateTimeOffset CachedAtUtc);

public interface IFallbackCacheStore
{
    ValueTask<CachedResponseSnapshot?> GetAsync(string cacheKey, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string cacheKey, CachedResponseSnapshot snapshot, TimeSpan ttl, CancellationToken cancellationToken = default);
}

public sealed class FallbackCacheOptions
{
    public const string SectionName = "FallbackCache";

    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 300;
    public Dictionary<string, string> StaticFallbacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}