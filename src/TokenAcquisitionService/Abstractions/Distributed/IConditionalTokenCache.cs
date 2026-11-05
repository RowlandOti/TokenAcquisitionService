namespace TokenAcquisitionService;

public interface IConditionalTokenCache
{
    Task<CacheEnvelope?> ReadAsync(string key, CancellationToken cancellationToken);
    Task<long> ReserveFenceAsync(string key, CancellationToken cancellationToken);
    Task<bool> TryWriteAsync(string key, CacheEnvelope envelope, LeaseGrant? lease, CancellationToken cancellationToken);
}
