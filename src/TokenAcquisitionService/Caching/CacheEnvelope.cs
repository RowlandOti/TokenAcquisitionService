namespace TokenAcquisitionService;

public sealed record CacheEnvelope(string CacheKey, TokenResult Token, DateTimeOffset CacheUntil,
    long Generation = 0, long Fence = 0);
