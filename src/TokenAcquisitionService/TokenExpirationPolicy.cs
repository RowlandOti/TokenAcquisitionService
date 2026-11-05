namespace TokenAcquisitionService;

internal sealed class TokenExpirationPolicy(TimeProvider time, TokenCacheOptions options)
{
    public bool IsFresh(TokenResult? token) => IsFresh(token, time.GetUtcNow());

    private bool IsFresh(TokenResult? token, DateTimeOffset now) => token is not null
        && !string.IsNullOrWhiteSpace(token.AccessToken)
        && token.ExpiresAt - now > options.SafetyBuffer;

    public bool IsUsable(CacheEnvelope? entry, string key)
    {
        var now = time.GetUtcNow();
        return entry is not null && entry.CacheKey == key && entry.CacheUntil > now && IsFresh(entry.Token, now);
    }

    public DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl) =>
        Until(token, maximumTtl, time.GetUtcNow());

    public DateTimeOffset Until(TokenResult token, TimeSpan maximumTtl, DateTimeOffset now) =>
        Min(token.ExpiresAt - options.SafetyBuffer, now + maximumTtl);

    public static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
