namespace TokenAcquisitionService;

public sealed record TokenResult(string AccessToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"TokenResult {{ ExpiresAt = {ExpiresAt:O}, AccessToken = [REDACTED] }}";
}
