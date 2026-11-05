namespace TokenAcquisitionService;

public sealed record CacheEnvelope(string CacheKey, TokenResult Token, DateTimeOffset CacheUntil);
