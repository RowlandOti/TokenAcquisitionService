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
