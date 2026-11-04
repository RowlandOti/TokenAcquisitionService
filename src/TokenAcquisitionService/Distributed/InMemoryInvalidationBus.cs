using System.Collections.Concurrent;

namespace TokenAcquisitionService;

/// <summary>Lossy in-process Pub/Sub demonstration. Disconnected subscriptions do not replay messages.</summary>
public sealed class InMemoryInvalidationBus : IInvalidationBus
{
    private readonly ConcurrentDictionary<long, Subscription> _subscriptions = new();
    private long _nextId;

    private sealed class Subscription(InMemoryInvalidationBus owner, long id, Action<InvalidationMessage> receiver)
        : IInvalidationSubscription
    {
        private int _connected = 1;
        public bool Connected { get => Volatile.Read(ref _connected) != 0; set => Volatile.Write(ref _connected, value ? 1 : 0); }
        public void Receive(InvalidationMessage message) { if (Connected) receiver(message); }
        public void Dispose()
        {
            Connected = false;
            owner._subscriptions.TryRemove(id, out _);
        }
    }

    public IInvalidationSubscription Subscribe(Action<InvalidationMessage> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        var id = Interlocked.Increment(ref _nextId);
        var subscription = new Subscription(this, id, receiver);
        _subscriptions[id] = subscription;
        return subscription;
    }

    public Task PublishAsync(InvalidationMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var subscription in _subscriptions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            subscription.Receive(message);
        }
        return Task.CompletedTask;
    }
}
