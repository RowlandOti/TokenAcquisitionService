namespace TokenAcquisitionService;

public interface ITokenService
{
    Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken = default);
}
