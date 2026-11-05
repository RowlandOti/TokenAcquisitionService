namespace TokenAcquisitionService;

public sealed class AcquisitionCapacityException() : Exception("Too many distinct token acquisitions are pending.");
