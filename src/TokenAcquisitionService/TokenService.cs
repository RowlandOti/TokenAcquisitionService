using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace TokenAcquisitionService;

public sealed class TokenService : ITokenService, IDisposable, IAsyncDisposable
{
    private readonly ITokenSdk _sdk;
    private readonly ITokenCache _l2;
    private readonly TimeProvider _time;
    private readonly TokenCacheOptions _options;
    private readonly TokenExpirationPolicy _expiration;
    private readonly ResilienceOptions _resilience;
    private readonly TokenTelemetry _telemetry;
    private readonly L2Circuit _circuit;
    private readonly DistributedPolicy? _distributed;
    private readonly MemoryCache _l1 = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly object[] _localWriteStripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sdkSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Owning reference, caller waits, and physical workers keep resources alive through cancellation.
    private int _activities = 1;
    private int _disposed;
    private int _cleaned;
    private int _pendingKeys;

    public int ActiveFlightCount => _flights.Count;

    public TokenService(ITokenSdk sdk, ITokenCache l2, TimeProvider timeProvider,
        TokenCacheOptions options, ILogger<TokenService> logger, ResilienceOptions? resilience = null,
        DistributedConfiguration? distributed = null)
    {
        options.Validate();
        _resilience = resilience ?? new ResilienceOptions();
        _resilience.Validate();
        _sdk = sdk;
        _l2 = l2;
        _time = timeProvider;
        _options = options;
        _expiration = new(timeProvider, options);
        _telemetry = new(logger);
        _circuit = new(timeProvider, _resilience, _telemetry);
        _sdkSlots = new(_resilience.MaximumConcurrentSdkCalls, _resilience.MaximumConcurrentSdkCalls);
        if (distributed is not null)
        {
            _distributed = new(distributed, timeProvider, options, _expiration, _telemetry);
            _distributed.Invalidated += EvictLocal;
        }
    }

    public async Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(request);
            using var callerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            cancellationToken = callerLifetime.Token;
            cancellationToken.ThrowIfCancellationRequested();
            var key = request.CacheKey;
            var forceUncached = false;
            for (var attempt = 0; ; attempt++)
            {
                var generation = forceUncached ? -1L : 0L;
                if (_distributed is not null && !forceUncached)
                {
                    try { generation = await _distributed.GenerationAsync(key, cancellationToken).ConfigureAwait(false); }
                    catch (DistributedDependencyException) when (
                        _distributed.Options.FailurePolicy == DistributedFailurePolicy.FreshSdkWithoutCaching)
                    {
                        _telemetry.Record("distributed_fallback", true);
                        generation = -1;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (generation >= 0 && ReadLocal(key, generation) is { } local) return Validate(local);
                var flightKey = $"{key}@{generation}";
                if (!_flights.TryGetValue(flightKey, out var flight))
                {
                    if (Interlocked.Increment(ref _pendingKeys) > _resilience.MaximumPendingKeys)
                    {
                        Interlocked.Decrement(ref _pendingKeys);
                        _telemetry.Record("capacity_throttle", true);
                        throw new AcquisitionCapacityException();
                    }
                    var candidate = new Flight(this, request, key, flightKey, generation);
                    // Example: 100 misses for the same key and generation select one lazy shared fill.
                    flight = _flights.GetOrAdd(flightKey, candidate);
                    if (!ReferenceEquals(flight, candidate)) Interlocked.Decrement(ref _pendingKeys);
                }
                if (flight.Work.IsValueCreated) _telemetry.Record("lock_contention");
                try
                {
                    // Callers share the fill without blocking threads; each may cancel only its own wait.
                    var envelope = await flight.Work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_distributed is not null && envelope.Generation > 0 && _distributed.Options.ValidateVersions
                        && await _distributed.GenerationAsync(key, cancellationToken).ConfigureAwait(false) != envelope.Generation)
                        throw new CacheConsistencyException();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Validate(envelope.Token);
                }
                catch (CacheConsistencyException) when (_distributed is not null
                    && attempt < _distributed.Options.MaximumConsistencyRetries)
                {
                    _telemetry.Record("invalidation_recovery");
                }
                catch (DistributedDependencyException) when (_distributed?.Options.FailurePolicy
                    == DistributedFailurePolicy.FreshSdkWithoutCaching)
                {
                    _telemetry.Record("distributed_fallback", true);
                    forceUncached = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _telemetry.Record("cancellation");
            throw;
        }
        finally { ExitActivity(); }
    }

    private sealed class Flight
    {
        public string Key { get; }
        public string FlightKey { get; }
        public long Generation { get; }
        public Lazy<Task<CacheEnvelope>> Work { get; }
        public Flight(TokenService owner, TokenRequest request, string key, string flightKey, long generation)
        {
            Key = key;
            FlightKey = flightKey;
            Generation = generation;
            Work = new(() =>
            {
                var task = owner.RunFlightAsync(this, request);
                L2Circuit.Observe(task);
                return task;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private async Task<CacheEnvelope> RunFlightAsync(Flight flight, TokenRequest request)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            using var timeout = new CancellationTokenSource(_resilience.AcquisitionTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, timeout.Token);
            try
            {
                if (_distributed is null)
                {
                    var legacy = await FillAsync(flight.Key, request, linked.Token).ConfigureAwait(false);
                    return new(flight.Key, legacy, Until(legacy, _options.L2MaximumTtl));
                }
                if (flight.Generation >= 0)
                {
                    try
                    {
                        var entry = await _distributed.FillAsync(flight.Key, flight.Generation,
                            ct => AcquireSdkAsync(request, ct), linked.Token).ConfigureAwait(false);
                        Validate(entry.Token);
                        WriteLocal(flight.Key, entry.Token, entry.CacheUntil, entry.Generation, entry.Fence);
                        return entry;
                    }
                    catch (DistributedDependencyException) when (
                        _distributed.Options.FailurePolicy == DistributedFailurePolicy.FreshSdkWithoutCaching)
                    {
                        _telemetry.Record("distributed_fallback", true);
                    }
                }
                var uncached = await AcquireSdkAsync(request, linked.Token).ConfigureAwait(false);
                return new(flight.Key, uncached, Until(uncached, _options.L2MaximumTtl), -1);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                _telemetry.Record("sdk_timeout", true);
                throw new TimeoutException("Token acquisition exceeded its deadline.");
            }
        }
        finally
        {
            // A completed worker must never remove a replacement flight for the same key.
            // Remove completed or failed work so later callers can recheck caches and retry.
            _flights.TryRemove(new KeyValuePair<string, Flight>(flight.FlightKey, flight));
            Interlocked.Decrement(ref _pendingKeys);
            ExitActivity();
        }
    }

    private async Task<TokenResult> FillAsync(string key, TokenRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Recheck L1 before filling: a completed predecessor may have populated it since the initial miss.
        if (ReadLocal(key) is { } cached) return Validate(cached);
        CacheEnvelope? remote = null;
        try
        {
            remote = await _circuit.ExecuteAsync(ct => _l2.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _telemetry.Record("l2_failure", true);
            // Best effort permits SDK fallback on L2 failure; strict mode and cancellation still propagate.
            if (!_options.BestEffortL2) throw;
            _telemetry.Record("fallback", true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (IsUsable(remote, key))
        {
            _telemetry.Record("l2_hit");
            WriteLocal(key, remote!.Token, remote.CacheUntil);
            return Validate(remote.Token);
        }
        _telemetry.Record("miss");
        var token = await AcquireSdkAsync(request, cancellationToken).ConfigureAwait(false);
        var envelope = new CacheEnvelope(key, token, Until(token, _options.L2MaximumTtl));
        try
        {
            await _circuit.ExecuteAsync(async ct =>
            {
                await _l2.SetAsync(key, envelope, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken, write: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _telemetry.Record("l2_failure", true);
            // Best effort permits a fresh SDK result and L1 caching despite failed L2 persistence.
            if (!_options.BestEffortL2) throw;
            _telemetry.Record("fallback", true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Validate(token);
        WriteLocal(key, token, envelope.CacheUntil);
        return Validate(token);
    }

    private async Task<TokenResult> AcquireSdkAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        var sdkTask = InvokeSdkAsync(request, cancellationToken);
        L2Circuit.Observe(sdkTask);
        return Validate(await sdkTask.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<TokenResult> InvokeSdkAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activities);
        var entered = false;
        try
        {
            if (_sdkSlots.CurrentCount == 0) _telemetry.Record("capacity_throttle");
            await _sdkSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _telemetry.Record("sdk_acquisition");
                var started = _time.GetTimestamp();
                try
                {
                    try { return await _sdk.AcquireAsync(request, cancellationToken).ConfigureAwait(false); }
                    finally { _telemetry.SdkDuration(_time.GetElapsedTime(started)); }
                }
                catch (TransientTokenException) when (attempt < _resilience.MaximumSdkRetries)
                {
                    _telemetry.Record("sdk_failure", true);
                    _telemetry.Record("sdk_retry");
                    var factor = _resilience.UseRetryJitter ? 0.5 + Random.Shared.NextDouble() : 1;
                    var milliseconds = Math.Min(_resilience.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt) * factor,
                        _resilience.MaximumRetryDelay.TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _time, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _telemetry.Record("sdk_failure", true);
                    throw;
                }
            }
        }
        finally
        {
            // An uncooperative SDK retains its slot until actual completion, even after callers time out.
            if (entered) _sdkSlots.Release();
            ExitActivity();
        }
    }

    private TokenResult? ReadLocal(string key, long generation = 0)
    {
        if (!_l1.TryGetValue<CacheEnvelope>(key, out var entry) || !IsUsable(entry, key)) return null;
        if (generation != 0 && entry!.Generation != generation)
        {
            _telemetry.Record("version_mismatch");
            return null;
        }
        _telemetry.Record("l1_hit");
        return entry!.Token;
    }

    private bool IsUsable(CacheEnvelope? entry, string key)
    {
        var usable = _expiration.IsUsable(entry, key);
        if (entry is not null && !usable) _telemetry.Record("freshness_rejection");
        return usable;
    }

    private TokenResult Validate(TokenResult token)
    {
        if (!_expiration.IsFresh(token))
        {
            _telemetry.Record("freshness_rejection");
            throw new UnusableTokenException();
        }
        return token;
    }

    private DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl) =>
        _expiration.Until(token, maximumTtl);

    // Example: At 10:34, L1's five-minute cap allows caching until 10:39. If L2's
    // CacheUntil is 10:38, Min caps L1 at 10:38: a four-minute TTL, unless token freshness requires less.
    private void WriteLocal(string key, TokenResult token, DateTimeOffset remoteUntil, long generation = 0, long fence = 0)
    {
        // One sample keeps the logical deadline and its relative TTL consistent.
        var now = _time.GetUtcNow();
        var until = TokenExpirationPolicy.Min(_expiration.Until(token, _options.L1MaximumTtl, now), remoteUntil);
        var ttl = until - now;
        if (ttl <= TimeSpan.Zero) return;
        // Fixed stripes only cover an in-memory compare/set, never I/O or acquisition.
        // Different generation flights may overlap; a delayed old fill must not replace a newer L1.
        var stripe = _localWriteStripes[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_localWriteStripes.Length];
        lock (stripe)
        {
            if (_l1.TryGetValue<CacheEnvelope>(key, out var existing)
                && (existing!.Generation > generation || (existing.Generation == generation && existing.Fence > fence)))
                return;
            _l1.Set(key, new CacheEnvelope(key, token, until, generation, fence),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
        }
    }

    public async Task InvalidateAsync(TokenRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(request);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            if (_distributed is null) throw new InvalidOperationException("Distributed features are not configured.");
            await _distributed.InvalidateAsync(request.CacheKey, lifetime.Token).ConfigureAwait(false);
        }
        finally { ExitActivity(); }
    }

    private void EvictLocal(string key)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _telemetry.Record("invalidation_received");
                _distributed?.ForgetVersion(key);
                _l1.Remove(key);
            }
        }
        finally { ExitActivity(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_distributed is not null) _distributed.Invalidated -= EvictLocal;
        try { _shutdown.Cancel(); }
        finally { ExitActivity(); }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _drained.Task.ConfigureAwait(false);
    }

    private void ExitActivity()
    {
        if (Interlocked.Decrement(ref _activities) != 0 || Volatile.Read(ref _disposed) == 0
            || Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        _l1.Dispose();
        _distributed?.Dispose();
        _sdkSlots.Dispose();
        _shutdown.Dispose();
        _telemetry.Dispose();
        _drained.TrySetResult();
    }
}
