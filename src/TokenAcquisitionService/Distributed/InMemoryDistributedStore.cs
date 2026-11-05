using System.Collections.Concurrent;

namespace TokenAcquisitionService;

/// <summary>Demonstration adapter: atomic per-key authority, leases, and writes in one process.</summary>
public sealed class InMemoryDistributedStore(TimeProvider time)
    : IVersionAuthority, IConditionalTokenCache, IDistributedLeaseProvider, ITokenCache
{
    private sealed class State
    {
        public long? Generation = 1;
        public long Fence;
        public long CommittedFence;
        public LeaseGrant? Lease;
        public CacheEnvelope? Entry;
    }
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private State Get(string key) => _states.GetOrAdd(key, _ => new State());

    public Task<long?> ReadVersionAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(key);
        lock (state) return Task.FromResult(state.Generation);
    }

    public Task<long> InvalidateAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(key);
        lock (state)
        {
            if (state.Generation is not { } generation) throw new MissingVersionException();
            state.Generation = checked(generation + 1);
            state.Fence = checked(state.Fence + 1);
            state.Entry = null;
            state.Lease = null;
            return Task.FromResult(state.Generation.Value);
        }
    }

    public Task<CacheEnvelope?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(key);
        lock (state)
        {
            var entry = state.Entry;
            return Task.FromResult(entry is not null && entry.CacheUntil > time.GetUtcNow() ? entry : null);
        }
    }

    public Task<long> ReserveFenceAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(key);
        lock (state)
        {
            // A non-lease reservation explicitly fences any outstanding lease holder.
            state.Lease = null;
            return Task.FromResult(state.Fence = checked(state.Fence + 1));
        }
    }

    public Task<LeaseGrant?> TryAcquireAsync(string key, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var state = Get(key);
        lock (state)
        {
            if (state.Lease is { } active && active.ExpiresAt > time.GetUtcNow())
                return Task.FromResult<LeaseGrant?>(null);
            state.Fence = checked(state.Fence + 1);
            state.Lease = new(key, Guid.NewGuid(), state.Fence, time.GetUtcNow() + duration);
            return Task.FromResult<LeaseGrant?>(state.Lease);
        }
    }

    public Task<bool> ReleaseAsync(LeaseGrant lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(lease.Key);
        lock (state)
        {
            if (state.Lease != lease) return Task.FromResult(false);
            state.Lease = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryWriteAsync(string key, CacheEnvelope envelope, LeaseGrant? lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Get(key);
        lock (state)
        {
            // Generation, fence, and ownership are tested in the same atomic operation as replacement.
            if (state.Generation != envelope.Generation || envelope.Fence != state.Fence
                || envelope.Fence <= state.CommittedFence || envelope.CacheKey != key
                || envelope.CacheUntil <= time.GetUtcNow() || envelope.Token is null || envelope.Token.ExpiresAt <= time.GetUtcNow()
                || string.IsNullOrWhiteSpace(envelope.Token.AccessToken)
                || (lease is null && state.Lease is not null)
                || (lease is not null && (state.Lease != lease || lease.Key != key
                    || lease.Fence != envelope.Fence || lease.ExpiresAt <= time.GetUtcNow())))
                return Task.FromResult(false);
            state.Entry = envelope;
            state.CommittedFence = envelope.Fence;
            return Task.FromResult(true);
        }
    }

    public Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken) => ReadAsync(key, cancellationToken);
    public Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This store requires atomic conditional writes.");
}
