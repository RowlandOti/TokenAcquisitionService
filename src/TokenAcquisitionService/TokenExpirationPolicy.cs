namespace TokenAcquisitionService;

internal sealed class TokenExpirationPolicy(TimeProvider time, TokenCacheOptions options)
{
    public bool IsFresh(TokenResult? token) => token is not null
        && !string.IsNullOrWhiteSpace(token.AccessToken)
        && token.ExpiresAt - time.GetUtcNow() > options.SafetyBuffer;

    public bool IsUsable(CacheEnvelope? entry, string key) => entry is not null
        && entry.CacheKey == key && entry.CacheUntil > time.GetUtcNow() && IsFresh(entry.Token);

    public DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl) =>
        Min(token.ExpiresAt - options.SafetyBuffer, time.GetUtcNow() + maximumTtl);

    public static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
