using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace TokenAcquisitionService.Tests;

internal sealed class DistributedHarness : IDisposable
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public InMemoryDistributedStore Store { get; }
    public DistributedFaults Faults { get; }
    public FakeSdk Sdk { get; }
    public InMemoryInvalidationBus Bus { get; } = new();
    private readonly List<TokenService> _services = [];
    public DistributedHarness()
    {
        Store = new(Time);
        Faults = new(Store);
        Sdk = new() { Handler = (_, _) => Task.FromResult(Token()) };
    }
    public TokenResult Token(string value = "distributed-token") => new(value, Time.GetUtcNow().AddHours(1));
    public TokenService NewService(DistributedOptions? options = null, IInvalidationBus? bus = null)
    {
        var service = new TokenService(Sdk, Store, Time, new(), NullLogger<TokenService>.Instance,
            distributed: new(Faults, Faults, Faults, bus, options));
        _services.Add(service);
        return service;
    }
    public CacheEnvelope Envelope(string key, long generation, long fence, string value = "value") =>
        new(key, Token(value), Time.GetUtcNow().AddMinutes(5), generation, fence);
    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
    }
}
