namespace TokenAcquisitionService;

public interface ITokenCache
{
    Task<CacheEnvelope?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, CacheEnvelope envelope, CancellationToken cancellationToken);
}
