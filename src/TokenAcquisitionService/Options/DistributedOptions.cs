namespace TokenAcquisitionService;

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
