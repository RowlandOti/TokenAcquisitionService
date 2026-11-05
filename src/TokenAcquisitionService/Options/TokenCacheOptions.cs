namespace TokenAcquisitionService;

public sealed record TokenCacheOptions
{
    public TimeSpan SafetyBuffer { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan L1MaximumTtl { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan L2MaximumTtl { get; init; } = TimeSpan.FromMinutes(30);
    public bool BestEffortL2 { get; init; } = true;

    internal void Validate()
    {
        if (SafetyBuffer < TimeSpan.Zero || L1MaximumTtl <= TimeSpan.Zero || L2MaximumTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TokenCacheOptions));
    }
}
