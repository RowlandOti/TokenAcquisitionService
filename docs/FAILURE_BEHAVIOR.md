# E. Failure behavior

**Branch 2 update:** storage fallback and cancellation exclusions remain. Shared failures now
fan out to existing waiters, and initiating-caller cancellation only cancels its wait. Service
shutdown/deadlines govern the acquisition. Timeouts, classified retries, circuit recovery, and
draining are described in [production resilience](PRODUCTION_RESILIENCE.md); the remainder
records the foundation behavior for comparison.

## Question and selected policy

What happens when L2 or the SDK fails, and how does cancellation propagate?
`BestEffortL2=true` (default) falls back to the SDK after an L2 read failure and returns a fresh
SDK result despite an L2 write failure. Warnings contain fixed messages, not exception payloads.
With `false`, storage failures propagate. SDK exceptions always propagate; invalid results
are never cached. No retry loop is implemented in Branch 1.

```csharp
catch (Exception error) when (error is not OperationCanceledException && options.BestEffortL2)
{
    logger.LogWarning("L2 read failed; falling back to SDK.");
}
```

## Invariant and guarantees

L1 hits do not access L2. Storage failure is never interpreted as an empty successful result
without a warning. Cancellation is excluded from storage fallback. An owner releases its gate
on SDK failure, L2 failure, invalid token, or cancellation. Subsequent callers can retry; no
faulted task or unusable token poisons the cache.

## Failure timeline

A owns key -> SDK throws -> `finally` releases gate -> B enters -> rechecks -> retries SDK.
Owner cancellation aborts the owner's acquisition if the SDK cooperates; B is not automatically
canceled. A canceled waiting caller never acquires/releases the gate. If cache data appeared
while the owner was canceled, B reads it instead of unnecessarily reacquiring.

## Trade-offs and alternatives

Best-effort caching favors availability but can amplify issuer traffic during an outage.
Fail-closed suits deployments requiring shared-cache availability but also fails good acquisitions.
No L2 timeout/circuit breaker is present yet. Never catch cancellation as generic cache failure.

An alternative is shared acquisition with caller-only wait cancellation and a service-owned
deadline; Branch 2 adopts it. Other alternatives include circuit breaking, bounded concurrency,
or classified transient retries. Permanent authentication errors should not be retried blindly.

## Corresponding tests and limits

`L1_works_during_L2_outage_and_best_effort_misses_fall_back`,
`Strict_L2_policy_propagates_read_and_write_failures`, `L2_cancellation_is_never_swallowed`,
`Sdk_failure_does_not_poison_cache_or_gate`,
`Cancellation_while_waiting_does_not_cancel_owner_or_block_later_callers`,
`Canceled_owner_releases_gate_and_waiter_rechecks_external_population`.

Branch 1 cannot bound an uncooperative SDK/storage operation or safely dispose during active
calls. Shutdown must first drain callers. L2 write failure can leave L1-only data; a restarted
service will reacquire. An SDK can also successfully issue a token before a canceled caller
observes it: cancellation does not roll back external side effects.
