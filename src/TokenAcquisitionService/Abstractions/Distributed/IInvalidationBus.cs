namespace TokenAcquisitionService;

public interface IInvalidationBus
{
    IInvalidationSubscription Subscribe(Action<InvalidationMessage> receiver);
    Task PublishAsync(InvalidationMessage message, CancellationToken cancellationToken);
}
