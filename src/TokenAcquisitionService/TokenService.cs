using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace TokenAcquisitionService;

public sealed class TokenService : ITokenService, IDisposable
{
    private readonly ITokenSdk _sdk;
    private readonly ITokenCache _l2;
    private readonly TimeProvider _time;
    private readonly TokenCacheOptions _options;
    private readonly ILogger<TokenService> _logger;
    private readonly MemoryCache _l1 = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public TokenService(ITokenSdk sdk, ITokenCache l2, TimeProvider timeProvider,
        TokenCacheOptions options, ILogger<TokenService> logger)
    {
        options.Validate();
        _sdk = sdk;
        _l2 = l2;
        _time = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var key = request.CacheKey;
        if (ReadLocal(key) is { } local) return Validate(local);
        // Example: 100 same-key callers miss L1; GetOrAdd gives all callers the same stored gate.
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        // One caller enters; the other 99 await a permit without blocking thread-pool threads.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // After the first caller fills L1, waiters enter and reuse it instead of calling the SDK.
            // The gate alone only serializes calls; this recheck prevents duplicate successful fills.
            if (ReadLocal(key) is { } cached) return Validate(cached);
            CacheEnvelope? remote = null;
            try
            {
                remote = await _l2.GetAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && _options.BestEffortL2)
            {
                // Best effort allows SDK fallback when L2 fails; cancellation and strict-mode failures propagate.
                _logger.LogWarning("L2 read failed; falling back to SDK.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (IsUsable(remote, key))
            {
                WriteLocal(key, remote!.Token, remote.CacheUntil);
                return Validate(remote.Token);
            }
            var token = Validate(await _sdk.AcquireAsync(request, cancellationToken).ConfigureAwait(false));
            var envelope = new CacheEnvelope(key, token, Until(token, _options.L2MaximumTtl));
            try
            {
                await _l2.SetAsync(key, envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && _options.BestEffortL2)
            {
                // Best effort still permits L1 caching and a fresh SDK result if L2 persistence fails.
                // Cancellation and strict-mode failures propagate instead.
                _logger.LogWarning("L2 write failed; returning SDK result without distributed caching.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Validate(token);
            WriteLocal(key, token, envelope.CacheUntil);
            return Validate(token);
        }
        finally
        {
            // Release on success, failure, or cancellation so the next waiter can recheck caches.
            gate.Release();
        }
    }

    private TokenResult? ReadLocal(string key)
    {
        if (_l1.TryGetValue<CacheEnvelope>(key, out var entry) && IsUsable(entry, key))
            return entry!.Token;
        return null;
    }

    private bool IsUsable(CacheEnvelope? entry, string key)
    {
        var now = _time.GetUtcNow();
        return entry is not null && entry.CacheKey == key && entry.CacheUntil > now
            && !string.IsNullOrWhiteSpace(entry.Token.AccessToken)
            && entry.Token.ExpiresAt - now > _options.SafetyBuffer;
    }

    private TokenResult Validate(TokenResult token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresAt - _time.GetUtcNow() <= _options.SafetyBuffer)
            throw new UnusableTokenException();
        return token;
    }

    private DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl) =>
        Min(token.ExpiresAt - _options.SafetyBuffer, _time.GetUtcNow() + maximumTtl);

    // Example: At 10:34, L1's five-minute cap allows caching until 10:39. If L2's
    // CacheUntil is 10:38, Min caps L1 at 10:38: a four-minute TTL, unless token freshness requires less.
    private void WriteLocal(string key, TokenResult token, DateTimeOffset remoteUntil)
    {
        // One sample keeps the logical deadline and its relative TTL consistent.
        var now = _time.GetUtcNow();
        var until = Min(Min(token.ExpiresAt - _options.SafetyBuffer,
            now + _options.L1MaximumTtl), remoteUntil);
        var ttl = until - now;
        if (ttl > TimeSpan.Zero)
            _l1.Set(key, new CacheEnvelope(key, token, until),
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = 1 });
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    // Branch 1 retains gates. Disposal requires callers to have drained; branch 2 adds active shutdown.
    public void Dispose()
    {
        _l1.Dispose();
        foreach (var gate in _gates.Values) gate.Dispose();
    }
}
