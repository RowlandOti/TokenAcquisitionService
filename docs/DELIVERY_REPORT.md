# Delivery report

## A. Git history

| Branch | Foundation checkpoint / parent |
|---|---|
| `feature/01-token-cache-foundation` | `05e23bc`, additive completion after root `ea55de0` |
| `feature/02-production-resilience` | `e3deb1a`, direct parent `05e23bc` |
| `feature/03-distributed-coordination` | Descends directly from `e3deb1a`; current checkout |

Use `git log --oneline --all --graph` and `git rev-parse <branch>` for final branch-tip hashes.
This document cannot embed its own final commit hash without changing that hash.
No existing repository content existed at initial inspection; no prior work was overwritten.
Resilience work was temporarily stashed while expanded foundation requirements were added,
then restored; no history was rewritten.

## B. Implementation

Foundation: immutable complete supplied security context, canonical JSON/SHA-256 keys,
L1/L2 cache-aside, expiry/buffer/TTL policy, per-key semaphore, configured L2 failure behavior,
and deliberately demonstrated distributed write limitations.

Resilience: bounded reclaimed shared flights, caller-only wait cancellation, shutdown/drain,
actual SDK capacity permits, read/write deadlines, circuit recovery, classified bounded exponential
retries with optional jitter, structured logs and metrics, and deterministic fault injection.

Coordination: optional distributed path; atomic generation advancement, lease/fence allocation,
conditional writes, stale-fill rejection, version-read modes, invalidation notifications, and
actual pinned HybridCache comparison tests. Original uncoordinated behavior is still tested.

## C. Validation

The local supported SDK is .NET 10.0.403; target is `net10.0`, with warnings treated as errors.
Development tools live only in ignored `.tools/`.

| Branch | Test cases | Failed / skipped |
|---|---:|---|
| Foundation | 47 | 0 / 0 |
| Resilience | 65 | 0 / 0 |
| Coordination | 101 | 0 / 0 |

Commands used for each acceptance gate:

```sh
dotnet restore TokenAcquisitionService.sln
dotnet build TokenAcquisitionService.sln --no-restore
dotnet test TokenAcquisitionService.sln --no-build --blame-hang-timeout 30s
```

Final branch validation additionally uses Release builds/tests and `dotnet format --verify-no-changes`.
On macOS long-running invocations are prefixed with `caffeinate -dimsu`; in this workspace the executable
is `.tools/dotnet/dotnet`. Tests use fake time/completion sources and require no network/credentials.
An initial HybridCache comparison exposed its exact-type memory-cache L2 optimization; the final
comparison uses a distinct shared adapter and verifies serialized L2 behavior rather than
mistaking L1-only behavior for distributed evidence.

## D. Key architectural choices

Small replaceable contracts keep SDK and infrastructure external. Canonicalization is deliberately
conservative; absolute expiry protects validity independently of cache residence. Shared tasks improve
cancellation isolation; physical SDK permits protect against uncooperative timeout overcommit.
Fences order write authority, generations order invalidation, notifications are only hints.
The [decision log](DESIGN_DECISIONS.md) and [branch comparison](BRANCH_COMPARISON.md) explain costs.

## E. Security assumptions

The SDK adapter must honor every supplied context field. Store and version authority must be trusted,
protected, and atomically consistent. Hashes are not encryption. A matching forged envelope cannot
prove opaque token claims. No token, context, or exception payload is logged by the service.
Safety is checked against the configured clock/buffer immediately before return, not forever.
Cache invalidation is not issuer-side token revocation.

## F. Distributed guarantees

The simulation proves controlled schedules only. Conditional operations reject stale generations,
fences and expired/replaced grants. Leases reduce duplicate work but cannot prevent old code from
running or guarantee exactly-once SDK effects. Version leases trade lookup cost for conditional
staleness; strict reads are still snapshots, not transactions spanning downstream token use.
See [consistency model](CONSISTENCY_MODEL.md).

## G. Coverage and remaining boundaries

All implementation families are mapped in [requirements matrix](REQUIREMENTS_MATRIX.md).
Real cache/issuer/broker/encryption adapters and deployed partition/failover tests are explicitly
outside the infrastructure-free scope. HybridCache is evaluated rather than shipping an incomplete
token adapter. Uncooperative SDK code may prevent async disposal from draining, while retained permits
keep physical concurrency bounded. Authority state needs durable monotonic retention in production.

Private publication was authorized after the initial no-push instruction. GitHub CLI was missing
and installed locally; browser authentication is required before repository creation/push. No
public repository or unverified remote will be used. Actual publication status is reported separately
after attempting authentication; local commits do not imply GitHub publication.

## H. Interview preparation

Five likely questions:

1. Which SDK request attributes may share a cache entry?
2. How is usable lifetime different from residence TTL?
3. Why recheck caches after acquiring ownership?
4. Who owns cancellation when callers share a task?
5. What happens when L2 fails after successful issuance?

Five difficult follow-ups:

1. Why does lease expiration not imply exactly-once issuance?
2. How do fencing and invalidation generations differ?
3. What happens if generation 5 finishes after generation 6 invalidation?
4. How does a still-running disconnected instance recover missed invalidation?
5. Why does HybridCache not remove token-specific freshness and distributed consistency obligations?

Study `Contracts.cs`, `TokenExpirationPolicy.cs`, `TokenService.cs`, `Resilience.cs`,
`Distributed/InMemoryDistributedStore.cs`, then `Distributed/DistributedPolicy.cs`.
The [interview guide](INTERVIEW_GUIDE.md) contains more than 30 questions with code/tests,
alternatives and failure scenarios, plus a dedicated 30-minute walkthrough.

## I. Next improvements

Build real adapter contract/integration tests, validate SDK-specific identity and credential rotation,
protect persistence/transport, tune limits from measured workloads, assess authority retention/failover,
and add a mature resilience library only if required policy complexity justifies it.
Do not add distributed complexity where the simpler branch's guarantees are sufficient.
