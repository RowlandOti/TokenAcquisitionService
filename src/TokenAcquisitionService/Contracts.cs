using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace TokenAcquisitionService;

public sealed record TokenRequest
{
    public string Tenant { get; }
    public string Client { get; }
    public string? Principal { get; }
    public string Authority { get; }
    public string Flow { get; }
    public ImmutableArray<string> Scopes { get; }
    public ImmutableSortedDictionary<string, string> Parameters { get; }

    public TokenRequest(string tenant, string client, string? principal, string authority,
        string flow, IEnumerable<string> scopes, IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentNullException.ThrowIfNull(scopes);
        if (principal is not null) ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        Tenant = tenant;
        Client = client;
        Principal = principal;
        Authority = authority;
        Flow = flow;
        Scopes = scopes.Select(scope =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scope);
            return scope;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        Parameters = (parameters ?? []).ToImmutableSortedDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in Parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
        }
    }

    // JSON framing avoids delimiter collisions; case-sensitive values remain conservative.
    public string CacheKey => "tak:v1:" + Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Tenant,
            Client,
            Principal,
            Authority,
            Flow,
            Scopes,
            Parameters
        })));
}

public sealed record TokenResult(string AccessToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"TokenResult {{ ExpiresAt = {ExpiresAt:O}, AccessToken = [REDACTED] }}";
}

public sealed record CacheEnvelope(string CacheKey, TokenResult Token, DateTimeOffset CacheUntil,
    long Generation = 0, long Fence = 0);

public interface ITokenSdk
{
    Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken);
}

public interface ITokenService
{
    Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken = default);
}

public interface ITokenCache
{
    Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken);
}

public sealed record TokenCacheOptions
{
    public TimeSpan SafetyBuffer { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan L1MaximumTtl { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan L2MaximumTtl { get; init; } = TimeSpan.FromMinutes(30);
    public bool BestEffortL2 { get; init; } = true;

    internal void Validate()
    {
        if (SafetyBuffer < TimeSpan.Zero || L1MaximumTtl <= TimeSpan.Zero || L2MaximumTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TokenCacheOptions));
    }
}

public sealed class UnusableTokenException() : Exception("The token is empty, expired, or inside its safety buffer.");
