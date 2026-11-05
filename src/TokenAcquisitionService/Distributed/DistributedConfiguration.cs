namespace TokenAcquisitionService;

public sealed record DistributedConfiguration(
    IVersionAuthority Versions, IConditionalTokenCache Cache,
    IDistributedLeaseProvider? Leases = null, IInvalidationBus? Notifications = null,
    DistributedOptions? Options = null);
