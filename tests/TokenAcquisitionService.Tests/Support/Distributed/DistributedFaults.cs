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
