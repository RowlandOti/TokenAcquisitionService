namespace TokenAcquisitionService;

public interface IDistributedLeaseProvider
{
    Task<LeaseGrant?> TryAcquireAsync(string key, TimeSpan duration, CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(LeaseGrant lease, CancellationToken cancellationToken);
}
