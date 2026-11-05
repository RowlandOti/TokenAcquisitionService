namespace TokenAcquisitionService;

public sealed class DistributedDependencyException(Exception innerException)
    : Exception("A distributed coordination dependency is unavailable.", innerException);
