using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace TokenAcquisitionService.Tests;

internal sealed class Harness : IDisposable
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public FakeSdk Sdk { get; }
    public InMemoryTokenCache Store { get; }
    public CacheDouble Cache { get; }
    public TokenService Service { get; }
    public TokenCacheOptions Options { get; }
    public Harness(TokenCacheOptions? options = null)
    {
        Options = options ?? new TokenCacheOptions();
        Store = new(Time);
        Cache = new(Store);
        Sdk = new() { Handler = (_, _) => Task.FromResult(Token()) };
        Service = NewService();
    }
    public TokenResult Token(string value = "test-token", int minutes = 60) =>
        new(value, Time.GetUtcNow().AddMinutes(minutes));
    public TokenService NewService() => new(Sdk, Cache, Time, Options, NullLogger<TokenService>.Instance);
    public static TokenRequest Request(string principal = "alice", string tenant = "tenant", string client = "client",
        string authority = "https://issuer.example/tenant", string flow = "delegated", string[]? scopes = null,
        KeyValuePair<string, string>[]? parameters = null) =>
        new(tenant, client, principal, authority, flow, scopes ?? ["read"], parameters);
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() => Service.Dispose();
}
