using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace TokenAcquisitionService.Tests;

internal sealed class FakeSdk : ITokenSdk
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public required Func<TokenRequest, CancellationToken, Task<TokenResult>> Handler { get; set; }
    public Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Handler(request, cancellationToken);
    }
}

internal sealed class CacheDouble(ITokenCache inner) : ITokenCache
{
    public Func<string, CancellationToken, Task<CacheEnvelope?>>? Read { get; set; }
    public Func<string, CacheEnvelope, CancellationToken, Task>? Write { get; set; }
    public Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken) =>
        Read is null ? inner.GetAsync(key, cancellationToken) : Read(key, cancellationToken);
    public Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken) =>
        Write is null ? inner.SetAsync(key, envelope, cancellationToken) : Write(key, envelope, cancellationToken);
}

internal sealed class Harness : IDisposable
{
    public FakeTimeProvider Time { get; }
    public FakeSdk Sdk { get; }
    public InMemoryTokenCache Store { get; }
    public CacheDouble Cache { get; }
    public TokenService Service { get; }
    public TokenCacheOptions Options { get; }
    public ResilienceOptions Resilience { get; }
    public Harness(TokenCacheOptions? options = null, ResilienceOptions? resilience = null, FakeTimeProvider? time = null)
    {
        Time = time ?? new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Options = options ?? new TokenCacheOptions();
        Resilience = resilience ?? new ResilienceOptions();
        Store = new(Time);
        Cache = new(Store);
        Sdk = new() { Handler = (_, _) => Task.FromResult(Token()) };
        Service = NewService();
    }
    public TokenResult Token(string value = "test-token", int minutes = 60) =>
        new(value, Time.GetUtcNow().AddMinutes(minutes));
    public TokenService NewService() => new(Sdk, Cache, Time, Options, NullLogger<TokenService>.Instance, Resilience);
    public static TokenRequest Request(string principal = "alice", string tenant = "tenant", string client = "client",
        string authority = "https://issuer.example/tenant", string flow = "delegated", string[]? scopes = null,
        KeyValuePair<string, string>[]? parameters = null) =>
        new(tenant, client, principal, authority, flow, scopes ?? ["read"], parameters);
    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() => Service.Dispose();
}
