using System.Collections.Concurrent;

namespace TokenAcquisitionService;

/// <summary>Thread-safe L2 simulation. Not a cross-process cache or a production storage adapter.</summary>
public sealed class InMemoryTokenCache(TimeProvider timeProvider) : ITokenCache
{
    private readonly ConcurrentDictionary<string, CacheEnvelope> _entries = new(StringComparer.Ordinal);

    public Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(key, out var entry) && entry.CacheUntil > timeProvider.GetUtcNow())
            return Task.FromResult<CacheEnvelope?>(entry);
        return Task.FromResult<CacheEnvelope?>(null);
    }

    public Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries[key] = envelope;
        return Task.CompletedTask;
    }
}
