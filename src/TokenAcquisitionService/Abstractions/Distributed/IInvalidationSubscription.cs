namespace TokenAcquisitionService;

public interface IInvalidationSubscription : IDisposable
{
    bool Connected { get; set; }
}
