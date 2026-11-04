# Distributed version stamps

## Two different orderings

`Generation` is an invalidation epoch. `Fence` orders cache-fill authority within/across epochs.
Neither is an invented issuer timestamp. Later token expiration does not imply a newer epoch
or greater authority. The store initializes a genuinely new key to generation 1 and advances
epochs/fences atomically under its per-key state lock.

`CacheEnvelope` carries both numbers. Authority metadata must outlive cache TTL. The simulation
retains it indefinitely; deleting/recreating metadata while old writers exist would break monotonic
ordering. A production adapter needs durable counters/tombstones, a namespace epoch, and a
careful retention strategy. Do not silently reset a known key to generation zero.

## Invalidation race

```text
A reads generation 5 -> A starts SDK
B atomically invalidates -> generation 6, old cache removed and leases fenced
B writes generation 6
A attempts generation 5 -> conditional write rejected
A rereads/retries -> can return only the allowed generation, subject to chosen version lease policy
```

`Generation_five_fill_cannot_become_authoritative_after_generation_six_invalidation` implements
this exact schedule. Version advancement, cache clearing, and fencing occur in the same authority
operation. No client-side read/increment/write is used.

## Read modes

| Mode | Cost / behavior | Guarantee / limitation |
|---|---|---|
| Strict (`ValidateVersions=true`, zero local lease) | Authority lookup before each cache read and after shared fills | Valid at an observed authority snapshot, not an atomic transaction spanning return |
| Local version lease | Cache version for configured interval; notifications can evict it | Reduced traffic, permits stale local data within that observation window |
| No per-hit validation | Authority on fill/write only; notifications optional | L1 can remain stale until residence expiry when messages are missed |

Version leases are anchored at **lookup start**, not response completion. A slow response cannot
extend the deadline. The physical `MemoryCache` may evict early, but logical checks use fakeable
time. Local lease metadata is size-limited to 10,000 entries. Duplicate/out-of-order notifications
evict rather than install versions, so they cannot roll authority backward.

Ten seconds of version leasing is **not a universal ten-second staleness guarantee**. The bound
depends on authoritative reads being current and available, invalidation being committed there,
clock/timer assumptions, in-flight snapshot timing, and no authority rollback. A strict reader can
also race an invalidation immediately after its final snapshot. Neither mode revokes a token
already returned to another system.

## Failure policy

Default `FailClosed` rejects dependency failure/missing metadata even with fresh L1. A previously
observed, unexpired local version lease remains usable until its deadline; on expiry, authority
must be consulted. Missing/null/nonpositive version metadata is an error, never proof of validity.

`FreshSdkWithoutCaching` favors availability: if authority/coordination fails, acquire directly from
the SDK, validate freshness, and do not read/write/promote cached data for that fallback.
This can increase issuance and forgo coordination. If failure happens after an SDK acquisition,
the fallback can cause another acquisition; it is deliberately not an exactly-once path.
SDK exceptions still propagate and are never disguised as authority failures.

`MaximumConsistencyRetries` bounds retries due to invalidation/fencing races (default 3).
Each shared flight has its ordinary acquisition deadline; repeated consistency retries can
consume multiple such deadlines. No unbounded version-race loop is permitted.

## Tests and alternatives

`DistributedTests` covers atomic generations, exact 5/6 race, version mismatch, missing metadata,
unavailable/timed-out authority, availability fallback, local lease hit/expiry/staleness, and
disabling per-hit version validation. `InvalidationTests` exercises missed-notification repair.
Alternative: version the physical cache key (`context:epoch`), retaining tombstones/epoch authority.
That simplifies stale fills but does not remove version-read, garbage-collection, or partition costs.
