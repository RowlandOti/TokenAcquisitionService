# Invalidation and notifications

`TokenService.InvalidateAsync` atomically advances the authority generation, clears shared data,
and fences in-flight writers, then evicts local caches and optionally publishes a notification.
Call it on a configured concrete service. It is intentionally not required by the ordinary
`ITokenService` acquisition-only interface.

The notification contains only canonical key and generation, never a token. Recipients evict
their L1 and version lease. They **do not install the message version as authoritative**.
Duplicate and out-of-order messages can cause redundant eviction but cannot regress authority.
Publication failure propagates after invalidation has already committed; no rollback is implied.
Invalidation permission is a deployment authorization concern.

```csharp
await service.InvalidateAsync(request, cancellationToken);
// The next lookup reads an allowed generation, or follows the explicit failure policy.
```

## Loss and recovery

`InMemoryInvalidationBus` is a clearly marked non-durable simulation. A disconnected subscription
does not receive or replay events. `SwitchableBus` is test-only fault injection.

```text
Running A caches generation 1 and disconnects
B invalidates -> generation 2; message missed by A
A with zero version lease checks authority on next read -> rejects generation 1
A with 10-second lease may reuse its cached observation until lease deadline
A with version checks disabled may reuse L1 until residence expiry
```

The risk is a **still-running disconnected** instance retaining L1, not a crashed process keeping
its memory after restart. A restart begins with empty L1 and uses shared L2/version state.

## Redis Pub/Sub and durable alternatives

Redis Pub/Sub is at-most-once delivery: disconnected or failed subscribers lose messages.
It is a latency optimization, not a durable invalidation authority.
Official reference: https://redis.io/docs/latest/develop/pubsub/

A durable event log can replay from a stored per-instance offset, subject to retention and
subscription lifecycle. Redis Streams consumer groups normally **distribute** work among members:
one shared group does not broadcast every invalidation to every cache-owning process. Broadcast
requirements need per-subscriber groups/subscriptions or another fanout/validation design.

Durable per-subscriber queues need acknowledgments, redelivery/idempotency, retention, recovery
offsets, subscriber provisioning/cleanup, and poison-message policy. They cost substantially more
than hints plus version checks. Short local TTLs can bound residence but are not instantaneous
invalidation or issuer revocation.

## Tests

`InvalidationTests` covers delivery, missed messages, duplicate/out-of-order messages, reconnection
without replay, local-version-lease repair, TTL-only repair, and delayed stale local fills.
The 5/6 generation race is in `DistributedTests`. No external broker is required.
