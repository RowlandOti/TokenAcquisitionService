namespace TokenAcquisitionService;

public sealed record LeaseGrant(string Key, Guid Owner, long Fence, DateTimeOffset ExpiresAt);
public sealed record InvalidationMessage(string Key, long Generation);

public interface IVersionAuthority
{
    Task<long?> ReadVersionAsync(string key, CancellationToken cancellationToken);
    Task<long> InvalidateAsync(string key, CancellationToken cancellationToken);
}

public interface IConditionalTokenCache
{
    Task<CacheEnvelope?> ReadAsync(string key, CancellationToken cancellationToken);
    Task<long> ReserveFenceAsync(string key, CancellationToken cancellationToken);
    Task<bool> TryWriteAsync(string key, CacheEnvelope envelope, LeaseGrant? lease, CancellationToken cancellationToken);
}

public interface IDistributedLeaseProvider
{
    Task<LeaseGrant?> TryAcquireAsync(string key, TimeSpan duration, CancellationToken cancellationToken);
    Task<bool> ReleaseAsync(LeaseGrant lease, CancellationToken cancellationToken);
}

public interface IInvalidationSubscription : IDisposable
{
    bool Connected { get; set; }
}

public interface IInvalidationBus
{
    IInvalidationSubscription Subscribe(Action<InvalidationMessage> receiver);
    Task PublishAsync(InvalidationMessage message, CancellationToken cancellationToken);
}

public enum DistributedFailurePolicy { FailClosed, FreshSdkWithoutCaching }

public sealed record DistributedOptions
{
    public bool EnableLeases { get; init; } = true;
    public bool ValidateVersions { get; init; } = true;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan AuthorityTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan LocalVersionLease { get; init; } = TimeSpan.Zero;
    public int MaximumConsistencyRetries { get; init; } = 3;
    public DistributedFailurePolicy FailurePolicy { get; init; } = DistributedFailurePolicy.FailClosed;

    internal void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero || AuthorityTimeout <= TimeSpan.Zero
            || LocalVersionLease < TimeSpan.Zero || MaximumConsistencyRetries is < 0 or > 10
            || !Enum.IsDefined(FailurePolicy))
            throw new ArgumentOutOfRangeException(nameof(DistributedOptions));
    }
}

public sealed record DistributedConfiguration(
    IVersionAuthority Versions, IConditionalTokenCache Cache,
    IDistributedLeaseProvider? Leases = null, IInvalidationBus? Notifications = null,
    DistributedOptions? Options = null);

public sealed class MissingVersionException() : Exception("Version metadata is missing; cached data cannot be trusted.");
public sealed class CacheConsistencyException() : Exception("Distributed state changed during acquisition; retry the request.");
