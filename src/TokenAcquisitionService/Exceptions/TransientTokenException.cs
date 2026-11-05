namespace TokenAcquisitionService;

/// <summary>Only SDK adapters may classify a failure as safe to retry.</summary>
public sealed class TransientTokenException(string message) : Exception(message);
