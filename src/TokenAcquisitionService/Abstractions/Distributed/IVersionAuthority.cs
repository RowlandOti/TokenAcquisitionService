namespace TokenAcquisitionService;

public interface IVersionAuthority
{
    Task<long?> ReadVersionAsync(string key, CancellationToken cancellationToken);
    Task<long> InvalidateAsync(string key, CancellationToken cancellationToken);
}
