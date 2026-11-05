namespace TokenAcquisitionService.Tests;

internal sealed class CacheDouble(ITokenCache inner) : ITokenCache
{
    public Func<string, CancellationToken, Task<CacheEnvelope?>>? Read { get; set; }
    public Func<string, CacheEnvelope, CancellationToken, Task>? Write { get; set; }
    public Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken) =>
        Read is null ? inner.GetAsync(key, cancellationToken) : Read(key, cancellationToken);
    public Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken) =>
        Write is null ? inner.SetAsync(key, envelope, cancellationToken) : Write(key, envelope, cancellationToken);
}
