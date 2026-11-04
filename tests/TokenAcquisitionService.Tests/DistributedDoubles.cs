using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace TokenAcquisitionService.Tests;

internal sealed class DistributedFaults(InMemoryDistributedStore store)
    : IVersionAuthority, IConditionalTokenCache, IDistributedLeaseProvider
{
    private int _versionReads;
    public int VersionReads => Volatile.Read(ref _versionReads);
    public Func<string, CancellationToken, Task<long?>>? VersionRead { get; set; }
    public Func<string, CancellationToken, Task<CacheEnvelope?>>? CacheRead { get; set; }
    public Func<string, CacheEnvelope, LeaseGrant?, CancellationToken, Task<bool>>? Write { get; set; }
    public Func<LeaseGrant, CancellationToken, Task<bool>>? Release { get; set; }
    public Task<long?> ReadVersionAsync(string key, CancellationToken ct)
    {
        Interlocked.Increment(ref _versionReads);
        return VersionRead is null ? store.ReadVersionAsync(key, ct) : VersionRead(key, ct);
    }
    public Task<long> InvalidateAsync(string key, CancellationToken ct) => store.InvalidateAsync(key, ct);
    public Task<CacheEnvelope?> ReadAsync(string key, CancellationToken ct) =>
        CacheRead is null ? store.ReadAsync(key, ct) : CacheRead(key, ct);
    public Task<long> ReserveFenceAsync(string key, CancellationToken ct) => store.ReserveFenceAsync(key, ct);
    public Task<bool> TryWriteAsync(string key, CacheEnvelope entry, LeaseGrant? lease, CancellationToken ct) =>
        Write is null ? store.TryWriteAsync(key, entry, lease, ct) : Write(key, entry, lease, ct);
    public Task<LeaseGrant?> TryAcquireAsync(string key, TimeSpan duration, CancellationToken ct) =>
        store.TryAcquireAsync(key, duration, ct);
    public Task<bool> ReleaseAsync(LeaseGrant lease, CancellationToken ct) =>
        Release is null ? store.ReleaseAsync(lease, ct) : Release(lease, ct);
}

internal sealed class SwitchableBus(InMemoryInvalidationBus inner) : IInvalidationBus
{
    public List<IInvalidationSubscription> Subscriptions { get; } = [];
    public IInvalidationSubscription Subscribe(Action<InvalidationMessage> receiver)
    {
        var subscription = inner.Subscribe(receiver);
        Subscriptions.Add(subscription);
        return subscription;
    }
    public Task PublishAsync(InvalidationMessage message, CancellationToken ct) => inner.PublishAsync(message, ct);
}

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
