using System.Threading.RateLimiting;

namespace IronGate.Domain;

/// <summary>
/// Abstraction for distributed rate limit storage.
/// Allows swapping between in-memory, Redis, SQL, etc.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Tries to acquire a permit for the given partition key.
    /// Returns a RateLimitLease indicating success/failure and metadata.
    /// </summary>
    ValueTask<RateLimitLease> AcquireAsync(string partitionKey, int permitCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current available permits for a partition key (for metrics/debugging).
    /// </summary>
    ValueTask<long> GetAvailablePermitsAsync(string partitionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets/clears the rate limit for a partition key.
    /// </summary>
    ValueTask ResetAsync(string partitionKey, CancellationToken cancellationToken = default);
}