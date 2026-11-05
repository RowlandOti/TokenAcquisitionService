namespace TokenAcquisitionService;

public sealed class UnusableTokenException() : Exception("The token is empty, expired, or inside its safety buffer.");
