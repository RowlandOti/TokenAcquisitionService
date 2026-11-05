namespace TokenAcquisitionService.Tests;

internal sealed class SwitchableBus(InMemoryInvalidationBus inner) : IInvalidationBus
{
    public List<IInvalidationSubscription> Subscriptions { get; } = [];
    public IInvalidationSubscription Subscribe(Action<InvalidationMessage> receiver)
    {
        var subscription = inner.Subscribe(receiver);
        Subscriptions.Add(subscription);
        return subscription;
    }
    public Task PublishAsync(InvalidationMessage message, CancellationToken ct) => inner.PublishAsync(message, ct);
}
