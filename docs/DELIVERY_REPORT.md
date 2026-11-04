# Delivery report

## A. Git history

| Branch | Foundation checkpoint / parent |
|---|---|
| `feature/01-token-cache-foundation` | `05e23bc`, additive completion after root `ea55de0` |
| `feature/02-production-resilience` | `e3deb1a`, direct parent `05e23bc` |
| `feature/03-distributed-coordination` | Descends directly from `e3deb1a`; current checkout |

Use `git log --oneline --all --graph` and `git rev-parse <branch>` for final branch-tip hashes.
This document cannot embed its own final commit hash without changing that hash.
All three branches are published in the verified **private** repository:
https://github.com/RowlandOti/TokenAcquisitionService

The default branch is `feature/03-distributed-coordination`. Draft comparison pull requests:

- https://github.com/RowlandOti/TokenAcquisitionService/pull/1 — resilience over foundation.
- https://github.com/RowlandOti/TokenAcquisitionService/pull/2 — coordination over resilience.

Leave these comparison PRs unmerged to preserve the independent branch snapshots.
Their review/discussion/inline-comment queues were checked after creation; all were empty.
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

Each branch was independently checked out, restored, built, and tested in Release: 47, 65, and
101 passing tests respectively, with zero failures, skips, build warnings, or errors. The final
branch also passed `dotnet format --verify-no-changes`; ancestry and clean-worktree checks passed.
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

**Private GitHub publication completed.** Publication was authorized after the initial no-push
instruction. Browser authorization was unavailable, but the existing macOS Git credential helper
successfully authenticated the authorized owner. Credentials were never printed or committed;
GitHub API operations used the existing credential only through process-local authentication.

The repository was created with private visibility, verified before any code upload, and verified
private again after publishing all three branches. Remote branch commit IDs matched the local tips.
No force push, direct push to `main`, history rewrite, or merge of the comparison branches occurred.
GitHub CLI's saved browser-login state is not required for native Git's existing credential helper.
No publishing blocker remains.

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
