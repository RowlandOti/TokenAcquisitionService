using Microsoft.Extensions.Caching.Memory;

namespace TokenAcquisitionService;

internal sealed class DistributedPolicy : IDisposable
{
    private readonly DistributedConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly TokenCacheOptions _cacheOptions;
    private readonly TokenExpirationPolicy _expiration;
    private readonly TokenTelemetry _telemetry;
    private readonly MemoryCache _versions = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly IInvalidationSubscription? _subscription;
    public DistributedOptions Options { get; }
    public event Action<string>? Invalidated;
    private sealed record VersionLease(long Generation, DateTimeOffset Until);

    public DistributedPolicy(DistributedConfiguration configuration, TimeProvider time,
        TokenCacheOptions cacheOptions, TokenExpirationPolicy expiration, TokenTelemetry telemetry)
    {
        _configuration = configuration;
        _time = time;
        _cacheOptions = cacheOptions;
        _expiration = expiration;
        _telemetry = telemetry;
        Options = configuration.Options ?? new();
        Options.Validate();
        if (Options.EnableLeases && configuration.Leases is null)
            throw new ArgumentException("Distributed leases are enabled but no provider is registered.", nameof(configuration));
        _subscription = configuration.Notifications?.Subscribe(message =>
        {
            // Notifications are hints: duplicate/out-of-order messages may cause harmless extra eviction.
            Invalidated?.Invoke(message.Key);
        });
    }

    public async Task<long> GenerationAsync(string key, CancellationToken cancellationToken)
    {
        if (!Options.ValidateVersions) return 0;
        if (_versions.TryGetValue<VersionLease>(key, out var cached) && cached!.Until > _time.GetUtcNow())
            return cached.Generation;
        var started = _time.GetUtcNow();
        var generation = await AuthoritativeGenerationAsync(key, cancellationToken).ConfigureAwait(false);
        // Anchor at request start, not response completion, so slow reads cannot extend a version lease.
        if (Options.LocalVersionLease > TimeSpan.Zero && started + Options.LocalVersionLease > _time.GetUtcNow())
            _versions.Set(key, new VersionLease(generation, started + Options.LocalVersionLease),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Options.LocalVersionLease });
        return generation;
    }

    private async Task<long> AuthoritativeGenerationAsync(string key, CancellationToken cancellationToken)
    {
        var version = await CallAsync(ct => _configuration.Versions.ReadVersionAsync(key, ct), cancellationToken)
            .ConfigureAwait(false);
        if (version is not > 0)
        {
            _telemetry.Record("version_store_failure", true);
            throw new DistributedDependencyException(new MissingVersionException());
        }
        return version.Value;
    }

    public async Task<CacheEnvelope> FillAsync(string key, long generation,
        Func<CancellationToken, Task<TokenResult>> acquire, CancellationToken cancellationToken)
    {
        if (generation == 0) generation = await AuthoritativeGenerationAsync(key, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await ReadAsync(key, generation, cancellationToken).ConfigureAwait(false);
            if (entry is not null) return entry;
            LeaseGrant? lease = null;
            long fence;
            if (Options.EnableLeases)
            {
                lease = await CallAsync(ct => _configuration.Leases!.TryAcquireAsync(key, Options.LeaseDuration, ct),
                    cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    _telemetry.Record("lease_contention");
                    await Task.Delay(Options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _telemetry.Record("lease_acquired");
                fence = lease.Fence;
            }
            else
                fence = await CallAsync(ct => _configuration.Cache.ReserveFenceAsync(key, ct), cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                // A former leader may have filled L2 just before we obtained ownership.
                entry = await ReadAsync(key, generation, cancellationToken).ConfigureAwait(false);
                if (entry is not null) return entry;
                if (await AuthoritativeGenerationAsync(key, cancellationToken).ConfigureAwait(false) != generation)
                    throw new CacheConsistencyException();
                _telemetry.Record("miss");
                var token = await acquire(cancellationToken).ConfigureAwait(false);
                var until = _expiration.Until(token, _cacheOptions.L2MaximumTtl);
                entry = new(key, token, until, generation, fence);
                if (await CallAsync(ct => _configuration.Cache.TryWriteAsync(key, entry, lease, ct), cancellationToken)
                    .ConfigureAwait(false))
                    return entry;
                _telemetry.Record("conditional_write_rejected", true);
                _telemetry.Record("fencing_rejection", true);
                if (lease is not null && lease.ExpiresAt <= _time.GetUtcNow()) _telemetry.Record("lease_expired", true);
                var winner = await ReadAsync(key, generation, cancellationToken).ConfigureAwait(false);
                return winner ?? throw new CacheConsistencyException();
            }
            finally
            {
                if (lease is not null)
                {
                    try
                    {
                        // Release has its own bounded lifetime; caller cancellation cannot strand ownership.
                        var released = await CallAsync(ct => _configuration.Leases!.ReleaseAsync(lease, ct),
                            CancellationToken.None).ConfigureAwait(false);
                        if (!released) _telemetry.Record("lease_ownership_failure", true);
                    }
                    catch (DistributedDependencyException)
                    {
                        _telemetry.Record("lease_release_failure", true);
                    }
                }
            }
        }
    }

    private async Task<CacheEnvelope?> ReadAsync(string key, long generation, CancellationToken cancellationToken)
    {
        var entry = await CallAsync(ct => _configuration.Cache.ReadAsync(key, ct), cancellationToken).ConfigureAwait(false);
        if (entry is null) return null;
        if (entry.Generation != generation)
        {
            _versions.Remove(key);
            _telemetry.Record("version_mismatch");
            throw new CacheConsistencyException();
        }

        if (!_expiration.IsUsable(entry, key))
        {
            _telemetry.Record("freshness_rejection");
            return null;
        }
        _telemetry.Record("l2_hit");
        return entry;
    }

    public void ForgetVersion(string key) => _versions.Remove(key);

    public async Task InvalidateAsync(string key, CancellationToken cancellationToken)
    {
        var version = await CallAsync(ct => _configuration.Versions.InvalidateAsync(key, ct), cancellationToken)
            .ConfigureAwait(false);
        _versions.Remove(key);
        Invalidated?.Invoke(key);
        if (_configuration.Notifications is not null)
            await CallAsync(async ct =>
            {
                await _configuration.Notifications.PublishAsync(new(key, version), ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        _telemetry.Record("invalidation_sent");
    }

    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(Options.AuthorityTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var task = operation(linked.Token);
            L2Circuit.Observe(task);
            return await task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _telemetry.Record("version_store_failure", true);
            throw new DistributedDependencyException(new TimeoutException("Distributed operation exceeded its deadline."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _telemetry.Record("version_store_failure", true);
            throw new DistributedDependencyException(exception);
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _versions.Dispose();
    }
}
