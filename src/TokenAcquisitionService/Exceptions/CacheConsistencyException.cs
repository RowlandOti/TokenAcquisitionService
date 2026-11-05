namespace TokenAcquisitionService;

public sealed class CacheConsistencyException() : Exception("Distributed state changed during acquisition; retry the request.");
