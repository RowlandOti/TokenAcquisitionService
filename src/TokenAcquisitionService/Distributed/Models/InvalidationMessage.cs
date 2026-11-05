namespace TokenAcquisitionService;

public sealed record InvalidationMessage(string Key, long Generation);
