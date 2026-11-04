# C. Concurrency

## Question and implementation

How do simultaneous misses avoid duplicate SDK acquisitions? Branch 1 uses a `SemaphoreSlim`
per canonical key. L1 is checked before waiting and again after entering. L2 is read inside
the gate, so a predecessor or another instance may populate it while a caller waits.

```csharp
await gate.WaitAsync(cancellationToken);
try
{
    // Recheck L1 and L2, then acquire and populate if still missing.
}
finally { gate.Release(); }
```

## Invariant, timeline, and guarantees

For one singleton service and key, at most one cache-fill operation is active.
A misses -> enters gate -> B misses -> waits -> A fills L2 then L1 -> A releases ->
B rechecks L1 -> no second SDK call. Different keys have independent gates. Release is in
`finally`; cancellation before entering does not incorrectly release a gate.
Local writes follow gate ownership, preventing a slower local fill overwriting a subsequent
local fill. No synchronous blocking of asynchronous work is used.

## Trade-offs, alternatives, and limits

The active owner's cancellation cancels its SDK call; waiting callers subsequently enter and
recheck caches or retry acquisition. Waiter cancellation affects only that waiter.
If the SDK ignores cancellation, the owner can delay all same-key callers.
Gates are retained until service disposal, which in Branch 1 must occur after callers drain.
This is intentionally simple but not suitable for unbounded key cardinality.

Shared-task singleflight can detach caller cancellation from shared acquisition, reducing retries.
Reference-counted gate dictionaries can safely reclaim gates, but removal must not create two
active gates for one key. Branch 2 addresses resource and cancellation limits.
There is no coordination across service instances, even inside one process with separate services.

## Corresponding tests

`One_hundred_same_key_callers_use_one_acquisition`,
`One_hundred_different_keys_acquire_independently`,
`Sdk_failure_does_not_poison_cache_or_gate`,
`Cancellation_while_waiting_does_not_cancel_owner_or_block_later_callers`,
`Canceled_owner_releases_gate_and_waiter_rechecks_external_population`,
`Expired_local_entry_is_rechecked_after_waiting`.
Tests use completion sources rather than sleeps; real-time waits are only deadlock guards.
