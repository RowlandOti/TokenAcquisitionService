namespace TokenAcquisitionService;

public sealed record LeaseGrant(string Key, Guid Owner, long Fence, DateTimeOffset ExpiresAt);
