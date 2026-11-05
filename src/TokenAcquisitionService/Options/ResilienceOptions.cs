namespace TokenAcquisitionService;

public sealed record ResilienceOptions
{
    public int MaximumConcurrentSdkCalls { get; init; } = 128;
    public int MaximumPendingKeys { get; init; } = 4096;
    public TimeSpan AcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan L2Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan? L2ReadTimeout { get; init; }
    public TimeSpan? L2WriteTimeout { get; init; }
    public int L2FailureThreshold { get; init; } = 3;
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumSdkRetries { get; init; } = 1;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public bool UseRetryJitter { get; init; }

    internal void Validate()
    {
        if (MaximumConcurrentSdkCalls <= 0 || MaximumPendingKeys <= 0 || L2FailureThreshold <= 0
            || AcquisitionTimeout <= TimeSpan.Zero || L2Timeout <= TimeSpan.Zero
            || L2ReadTimeout <= TimeSpan.Zero || L2WriteTimeout <= TimeSpan.Zero
            || CircuitOpenDuration <= TimeSpan.Zero || MaximumSdkRetries is < 0 or > 5 || RetryDelay < TimeSpan.Zero
            || MaximumRetryDelay <= TimeSpan.Zero || RetryDelay > MaximumRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(ResilienceOptions));
    }
}
