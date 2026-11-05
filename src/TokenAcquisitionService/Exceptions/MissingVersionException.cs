namespace TokenAcquisitionService;

public sealed class MissingVersionException() : Exception("Version metadata is missing; cached data cannot be trusted.");
