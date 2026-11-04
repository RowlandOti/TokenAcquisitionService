# Design decisions

These are intentional boundaries, not claims that the exercise is production complete.
Source paths below are under `src/TokenAcquisitionService`.

| Problem | Choice / code | Alternative | Benefit / drawback | Assumption, invariant, guarantee / limit |
|---|---|---|---|---|
| Secure identity | Immutable canonical request in `Contracts.cs` | SDK-derived key or typed flows | Portable, auditable; callers must include all SDK knobs | Trusted adapter; equivalent supplied contexts share, opaque token contents unverified |
| Key framing | Canonical JSON plus SHA-256 | Delimited strings, HMAC | Unambiguous and compact; not encryption | Hash collision resistance; no logged raw identity |
| Reuse | Cache-aside in `TokenService.AcquireAsync` | SDK-managed caching | Explicit L1/L2 policy; more orchestration code | L2 trusted; freshness on each read |
| Expiration | Absolute envelope/token deadlines | Sliding TTL, stale-on-error | Cannot extend token validity; can increase issuer load | Clock/buffer assumptions; no revocation guarantee |
| Local stampede | Per-key async semaphore | Global gate, striped locks, shared task | Independent keys; retained gate growth | One singleton per process; no distributed coordination |
| Write order | L2 atomic replacement | Conditional/fenced write | Simple contract; older write may win | Last-writer-wins only, not issuer ordering |
| Cancellation | Owner token governs acquisition | Service-owned shared lifetime | Simple, prompt cancellation; waiters may retry | Cooperative SDK; gate always released |
| L2 failure | Configurable best effort, fixed warnings | Always fail closed | Availability; outage amplifies issuer traffic | Never swallow cancellation or return unusable result |
| DI / ownership | Constructor-injected SDK/L2/clock/options/logger | Service locator/static state | Replaceable adapters; registrations required | Service owns L1/gates, container owns external dependencies |
| Layout | Small cohesive source files and feature test classes | One file per interface/folder | Easy interview navigation; larger files as features grow | Split only when responsibility warrants it |
| Simulation | Explicit `InMemoryTokenCache` demo adapter | Real Redis container | Deterministic infrastructure-free tests; no partition evidence | Dictionary atomicity is not proof of deployed distributed behavior |

## Complexity

Request canonicalization costs O(s log s + p log p + n), where s is scope count, p parameter
count, and n serialized length. Cache lookup/dictionary coordination is expected O(1), excluding
key creation and remote I/O. Memory is O(L1 entries + distinct gate keys + simulated L2 keys).
Branch 1 retains gates; L1 is size-limited; simulation L2 is deliberately not a production
eviction engine. Per-key serialized latency includes L2 read, SDK, and L2 write, not unrelated keys.

## Why cache-aside and the synchronization location?

L1 hits are an inexpensive fast path. On a miss, entering the per-key gate **before** reading L2
avoids 100 identical L2 reads and simplifies double-checking. The exact cache-aside pseudocode
need not be copied literally: the invariant is to recheck relevant state after obtaining ownership,
before invoking the SDK. There is no global acquisition lock.

Follow the five fundamental documents for code examples, timelines, guarantees, and tests.
