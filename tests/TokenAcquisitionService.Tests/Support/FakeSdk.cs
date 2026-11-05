namespace TokenAcquisitionService.Tests;

internal sealed class FakeSdk : ITokenSdk
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public required Func<TokenRequest, CancellationToken, Task<TokenResult>> Handler { get; set; }
    public Task<TokenResult> AcquireAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Handler(request, cancellationToken);
    }
}
