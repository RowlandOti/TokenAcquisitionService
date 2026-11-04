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
        if (ReadLocal(key) is { } local) return local;
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReadLocal(key) is { } cached) return cached;
            CacheEnvelope? remote = null;
            try
            {
                remote = await _l2.GetAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && _options.BestEffortL2)
            {
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
                _logger.LogWarning("L2 write failed; returning SDK result without distributed caching.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Validate(token);
            WriteLocal(key, token, envelope.CacheUntil);
            return Validate(token);
        }
        finally
        {
            gate.Release();
        }
    }

    private TokenResult? ReadLocal(string key)
    {
        if (_l1.TryGetValue<CacheEnvelope>(key, out var entry) && IsUsable(entry, key))
            return entry!.Token;
        return null;
    }

    private bool IsUsable(CacheEnvelope? entry, string key) =>
        entry is not null && entry.CacheKey == key && entry.CacheUntil > _time.GetUtcNow()
        && !string.IsNullOrWhiteSpace(entry.Token.AccessToken)
        && entry.Token.ExpiresAt - _time.GetUtcNow() > _options.SafetyBuffer;

    private TokenResult Validate(TokenResult token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresAt - _time.GetUtcNow() <= _options.SafetyBuffer)
            throw new UnusableTokenException();
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

    // Branch 1 retains gates. Disposal requires callers to have drained; branch 2 adds active shutdown.
    public void Dispose()
    {
        _l1.Dispose();
        foreach (var gate in _gates.Values) gate.Dispose();
    }
}
