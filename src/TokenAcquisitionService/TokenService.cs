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
    private readonly ResilienceOptions _resilience;
    private readonly TokenTelemetry _telemetry;
    private readonly L2Circuit _circuit;
    private readonly MemoryCache _l1 = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sdkSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activities = 1;
    private int _disposed;
    private int _cleaned;
    private int _pendingKeys;

    public int ActiveFlightCount => _flights.Count;

    public TokenService(ITokenSdk sdk, ITokenCache l2, TimeProvider timeProvider,
        TokenCacheOptions options, ILogger<TokenService> logger, ResilienceOptions? resilience = null)
    {
        options.Validate();
        _resilience = resilience ?? new ResilienceOptions();
        _resilience.Validate();
        _sdk = sdk;
        _l2 = l2;
        _time = timeProvider;
        _options = options;
        _telemetry = new(logger);
        _circuit = new(timeProvider, _resilience, _telemetry);
        _sdkSlots = new(_resilience.MaximumConcurrentSdkCalls, _resilience.MaximumConcurrentSdkCalls);
    }

    public async Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var key = request.CacheKey;
            if (ReadLocal(key) is { } local) return Validate(local);
            if (!_flights.TryGetValue(key, out var flight))
            {
                if (Interlocked.Increment(ref _pendingKeys) > _resilience.MaximumPendingKeys)
                {
                    Interlocked.Decrement(ref _pendingKeys);
                    _telemetry.Record("capacity_throttle", true);
                    throw new AcquisitionCapacityException();
                }
                var candidate = new Flight(this, request, key);
                flight = _flights.GetOrAdd(key, candidate);
                if (!ReferenceEquals(flight, candidate)) Interlocked.Decrement(ref _pendingKeys);
            }
            if (flight.Work.IsValueCreated) _telemetry.Record("lock_contention");
            var token = await flight.Work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Validate(token);
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
        public Lazy<Task<TokenResult>> Work { get; }
        public Flight(TokenService owner, TokenRequest request, string key)
        {
            Key = key;
            Work = new(() =>
            {
                var task = owner.RunFlightAsync(this, request);
                L2Circuit.Observe(task);
                return task;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private async Task<TokenResult> RunFlightAsync(Flight flight, TokenRequest request)
    {
        Interlocked.Increment(ref _activities);
        try
        {
            using var timeout = new CancellationTokenSource(_resilience.AcquisitionTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, timeout.Token);
            try
            {
                return await FillAsync(flight.Key, request, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                _telemetry.Record("sdk_timeout", true);
                throw new TimeoutException("Token acquisition exceeded its deadline.");
            }
        }
        finally
        {
            _flights.TryRemove(new KeyValuePair<string, Flight>(flight.Key, flight));
            Interlocked.Decrement(ref _pendingKeys);
            ExitActivity();
        }
    }

    private async Task<TokenResult> FillAsync(string key, TokenRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadLocal(key) is { } cached) return Validate(cached);
        CacheEnvelope? remote = null;
        try
        {
            remote = await _circuit.ExecuteAsync(ct => _l2.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _telemetry.Record("l2_failure", true);
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
        var sdkTask = InvokeSdkAsync(request, cancellationToken);
        L2Circuit.Observe(sdkTask);
        var token = Validate(await sdkTask.WaitAsync(cancellationToken).ConfigureAwait(false));
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
            if (!_options.BestEffortL2) throw;
            _telemetry.Record("fallback", true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Validate(token);
        WriteLocal(key, token, envelope.CacheUntil);
        return Validate(token);
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

    private TokenResult? ReadLocal(string key)
    {
        if (_l1.TryGetValue<CacheEnvelope>(key, out var entry) && IsUsable(entry, key))
        {
            _telemetry.Record("l1_hit");
            return entry!.Token;
        }
        return null;
    }

    private bool IsUsable(CacheEnvelope? entry, string key)
    {
        var usable = entry is not null && entry.CacheKey == key && entry.CacheUntil > _time.GetUtcNow()
        && !string.IsNullOrWhiteSpace(entry.Token.AccessToken)
        && entry.Token.ExpiresAt - _time.GetUtcNow() > _options.SafetyBuffer;
        if (entry is not null && !usable) _telemetry.Record("freshness_rejection");
        return usable;
    }

    private TokenResult Validate(TokenResult token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresAt - _time.GetUtcNow() <= _options.SafetyBuffer)
        {
            _telemetry.Record("freshness_rejection");
            throw new UnusableTokenException();
        }
        return token;
    }

    private DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl) =>
        Min(token.ExpiresAt - _options.SafetyBuffer, _time.GetUtcNow() + maximumTtl);

    private void WriteLocal(string key, TokenResult token, DateTimeOffset remoteUntil)
    {
        var until = Min(Until(token, _options.L1MaximumTtl), remoteUntil);
        var ttl = until - _time.GetUtcNow();
        if (ttl > TimeSpan.Zero)
            _l1.Set(key, new CacheEnvelope(key, token, until),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
        _sdkSlots.Dispose();
        _shutdown.Dispose();
        _telemetry.Dispose();
        _drained.TrySetResult();
    }
}
