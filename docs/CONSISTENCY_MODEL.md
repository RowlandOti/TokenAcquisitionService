# Consistency model

| Guarantee | Required assumptions | What is not promised |
|---|---|---|
| Correct security context | Complete canonical request and trustworthy SDK/storage adapters | Detecting opaque token substitution when all metadata is maliciously relabeled |
| Fresh at validation | Correct clock and configured safety buffer | Valid forever after return, issuer revocation |
| Local singleflight | One singleton service and same context/generation | Shared operation lasts until every caller returns |
| Healthy cross-instance coalescing | Common atomic lease authority, SDK completes inside lease, no fallback | Exactly-once issuance or effects |
| Lease mutual exclusion | Authority clock/atomic state is authoritative | No old process continues executing after expiration |
| Late-write rejection | Monotonic fencing retained and enforced atomically with write | Later expiration is greater authority |
| Invalidated fills cannot commit old epoch | Invalidation, fence, and write share one atomic authority | Comparing versions in separate nontransactional operations |
| Strict version read | Current, reachable version authority; no rollback | Linearizable application operation encompassing token use |
| Local lease bounded observation window | Correct authoritative snapshot, lease-start timing, no rollback | Universal bound under arbitrary partitions or stale replicas |
| Notification invalidates local caches | Delivery reaches a still-live subscription | Durable/replayed delivery; all instances see Pub/Sub |
| Restart clears local state | New service/process, shared L2 remains | A crashed process retains its in-memory L1 |
| Availability fallback returns fresh SDK result | SDK is reachable and correct | Distributed deduplication, invalidation ordering, or cache writes on that fallback |

## Ordering boundary

The simulation serializes authority operations per key. A conditional write is atomic relative
to invalidation and lease/fence changes. L1 reads are not distributed transactions: an instance
can observe generation N, then invalidation N+1 can commit before the token is returned.
This read is valid at its observed snapshot. Local version leasing intentionally permits a longer
stale observation window. Notifications improve latency, not correctness in strict mode.

Multiple valid tokens for a context can coexist. A new accepted fill fence does not immediately
invalidate every L1 copy in the same invalidation generation. Requiring “always the greatest fill
fence” would need additional per-hit validation or explicit invalidation; it is not claimed here.
Fences protect authoritative writes; versions protect invalidation epochs.

## Partitions and failure

Fail-closed trades availability for authority validation. Availability fallback deliberately bypasses
caches and returns a new SDK token. A fresh L1 hit in the ordinary (unconfigured) service remains
available during L2 outage; configured strict version mode intentionally has a stronger dependency.
An expired lease cannot stop an already running SDK call. Fencing stops its stale write, not its
issuance side effect.

Deleting a cached entry is **not token revocation**. Tokens already held by callers or resource
servers are outside this library's control. Use issuer/resource-server revocation features when
that guarantee is required.

Simulation tests demonstrate controlled schedules in one process. Real deployment additionally
needs failure-atomic persistence, topology/replication guarantees, namespaces, clocks, failover,
access controls, serialization contracts, and adapter integration tests.
