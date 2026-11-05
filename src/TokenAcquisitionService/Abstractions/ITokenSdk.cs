namespace TokenAcquisitionService;

public interface ITokenSdk
{
    Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken);
}
