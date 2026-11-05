# Interview guide

Branch 1 asks **Is my implementation correct?** Branch 2 asks **Does it remain reliable under
load and failure?** Branch 3 asks **What guarantees can multiple coordinating instances provide?**
Advanced questions below describe planned extensions when reading Branch 1. In Branch 2,
resource/cancellation/resilience extensions are implemented; distributed ones remain planned.

Locations abbreviate `src/TokenAcquisitionService/`; tests are in
`tests/TokenAcquisitionService.Tests/FoundationTests.cs` unless otherwise noted.

| # | Likely question / strong answer | Code / relevant test | Alternative and trade-off | Failure scenario / follow-up |
|---|---|---|---|---|
| 1 | Why a library? Callers need reusable acquisition, not an HTTP host. | `ITokenService`, `ServiceCollectionExtensions.cs`; restart test | Hosted API adds deployment/auth boundaries. | Container lifetime wrong; how register singleton? |
| 2 | Who issues tokens? Only the injected black-box SDK. | `ITokenSdk`; SDK failure test | SDK-specific wrapper reduces portability. | Adapter hides context; who validates it? |
| 3 | What identifies a token? All supplied security-context fields. | `TokenRequest`; `Security_context_is_isolated` | Typed flow models are stricter but larger. | Missing audience; how represent binding? |
| 4 | Why hash? Stable compact keys without delimiter ambiguity. | `CacheKey`; canonical identity test | Raw canonical JSON is inspectable but exposes identifiers. | Collision assumption; why might HMAC help? |
| 5 | Why sort scopes? Order/duplicates do not change a requested set. | `TokenRequest`; canonical identity test | SDK normalization may have other semantics. | Scope case matters; should casing change? |
| 6 | Normalize authority? Preserve exact values conservatively. | `TokenRequest`; context isolation test | URI normalization improves hit rate but can merge distinct contexts. | Alias misconfiguration; trusted discovery? |
| 7 | Is request state immutable? Inputs are copied into immutable collections. | `TokenRequest`; canonical identity test | Mutable DTO requires defensive copies per call. | Caller mutates array; what about nested data? |
| 8 | Can a wrong-user cache entry leak? Embedded key mismatch is rejected. | `IsUsable`; wrong-user envelope test | Authenticated envelopes defend stronger threat models. | Malicious correctly relabeled token; can opaque tokens be checked? |
| 9 | What is freshness? Expiry minus now minus buffer must be positive. | `Validate`; safety boundary theory | Refresh-ahead reduces latency at greater complexity. | Exactly at boundary; why reject equality? |
| 10 | Why two TTL caps? Residence policy differs from issuer lifetime. | `Until`; TTL caps test | One TTL is simpler but less tunable. | Long-lived token; how bound L1? |
| 11 | Why recalculate on promotion? Resetting L2 age can extend residence. | `WriteLocal`; promotion test | Carry one absolute deadline, fewer independent policies. | Near-expiry L2; what if transfer takes time? |
| 12 | Why injectable time? Boundary tests cannot depend on sleeping. | `TimeProvider`; expiry tests | Static clock hides nondeterminism. | Host clock skew; how large a buffer? |
| 13 | What if expiry occurs during I/O? Validate again before return. | `AcquireAsync`; expiring-during-write test | Validate only at lookup risks stale results. | Delayed persistence; should it retry? |
| 14 | Why per-key gates? Same-key serialization without blocking other contexts. | `_gates`; 100 same-key test | Global gate is simple but throttles unrelated requests. | Hot key; what is singleflight? |
| 15 | How show parallelism? Hold SDK calls at a completion source. | 100 different-key test | Sleep-based tests are flaky. | Hidden global serialization; how detect deterministically? |
| 16 | Why double-check? A predecessor can populate caches during a wait. | `AcquireAsync`; external population test | No recheck causes repeated acquisition. | First owner canceled; who resumes? |
| 17 | Is gate release safe? `finally` only after successful entry. | `AcquireAsync`; SDK failure test | Manual releases are easy to miss. | Wait canceled; why not release? |
| 18 | Who owns cancellation? In Branch 1 the active caller owns its SDK call. | `AcquireAsync`; cancellation tests | Shared task isolates waiters but needs shutdown/deadline. | Owner cancels; must followers retry? |
| 19 | How bound synchronization memory? Branch 1 does not reclaim gates. | `_gates`; branch 2 resource tests planned | Ref-count gates or shared flights cost lifecycle complexity. | Unbounded key cardinality; safe removal protocol? |
| 20 | Do semaphores coordinate hosts? No: only one service instance. | two-instance race test | Lease adds latency and failure modes. | Two simultaneous misses; exactly once? |
| 21 | Can old writes win? Yes in Branch 1, explicitly demonstrated. | race test; `InMemoryTokenCache` | Conditional writes need trustworthy generation. | Delayed leader; how fence it? |
| 22 | Is longer expiry newer? No, expiry is not authority/order. | race test; `RACE_CONDITIONS.md` | Issuer generation is stronger but may be unavailable. | Old long-lived token; what metadata is trustworthy? |
| 23 | What survives restart? Only shared L2, not local gates/L1. | restart test | Persist L1 complicates protection and lifecycle. | L2 write failed; what does restart do? |
| 24 | L2 offline? Fresh L1 works; configured fallback uses SDK. | outage and strict-policy tests | Fail-closed protects dependency policy, reduces availability. | Issuer overload; what should breaker do? |
| 25 | Retry SDK failures? Not in Branch 1; never blindly retry authentication errors. | SDK failure test | Classified retries cost latency and side effects. | Permanent invalid credential; what counts transient? |
| 26 | Hide exceptions? SDK errors propagate; storage fallback is logged. | `AcquireAsync`; failure tests | Exception payload logging risks secrets. | Sensitive SDK exception; what should telemetry contain? |
| 27 | Bounded acquisition? Branch 2 will separate caller wait from service lifetime. | branch 2 resilience tests planned | Owner cancellation is simpler but less shareable. | All callers cancel; continue or abort shared work? |
| 28 | How recover a breaker? Time-bound open interval plus one half-open probe. | branch 2 breaker tests planned | Retry every request amplifies outages. | Probe hangs; how release reservation? |
| 29 | Is a lease enough? No; expired leaders can still finish. | branch 3 lease tests planned | Fencing requires authority-side atomic enforcement. | Lease expires before SDK returns; discard or return? |
| 30 | Why versions? Detect invalidation and reject stale fills. | branch 3 version tests planned | Per-hit authority checks cost latency/availability. | Invalidation between read/write; can compare-and-set help? |
| 31 | Are notifications reliable? Pub/Sub can lose messages. | branch 3 notification tests planned | Durable log adds offsets/replay/storage. | Disconnected instance; version check or TTL recovery? |
| 32 | Is invalidation revocation? No; issued tokens can remain valid at issuer/resource. | trust boundary in README | Issuer-side revocation is outside cache control. | Already returned token; can cache invalidate it? |
| 33 | Why not HybridCache? Compare local coalescing and L1/L2 convenience, retain token-specific validation. | branch 3 comparison planned | Framework reduces code, not distributed consistency obligations. | Local stale entry; does remove broadcast? |
| 34 | What consistency is promised? Branch-specific, never blanket exactly-once. | race test; `RACE_CONDITIONS.md` | Stronger consistency costs authority availability and latency. | Partition; favor freshness or availability? |

## What I Would Change Next

## What I would explain in a 30-minute code review.

| Minutes | Walkthrough | Demonstration |
|---|---|---|
| 0-5 | SDK boundary, immutable request, conservative canonical identity | `Contracts.cs`, identity theory |
| 5-10 | Cache-aside, absolute expiration, two-minute safety worked example | `TokenService.cs`, `AcceptanceTests.Worked_1040_expiry_example_caps_promoted_L1` |
| 10-15 | Local singleflight, double-checking, independent keys | 100 same/different-key and thread-pool tests |
| 15-20 | Cancellation, SDK errors, L2 fallback and strict mode | Cancellation and outage tests |
| 20-25 | Two instances, older-write-wins, restart, trust boundary | Deterministic race and restart tests |
| 25-30 | Why branch 2/3 complexity is conditional on requirements | Resource lifecycle vs distributed ordering trade-offs |

### Async foundations for follow-up

| Question | Strong answer / code | Test | Alternative / trade-off | Failure / follow-up |
|---|---|---|---|---|
| What does `Task<TokenResult>` represent? | A future completion, not a dedicated thread; `ITokenSdk.AcquireAsync`. | SDK failure test | Synchronous SDK blocks capacity. | A fault is stored in the task; when observed? |
| What does `await` schedule? | Suspends only if incomplete, resumes by continuation; library awaits use `ConfigureAwait(false)`. | `Actual_thread_pool_contention_preserves_singleflight` | `Task.Run` is for scheduling CPU/blocking work, not required for async I/O. | Captured context deadlock; why no `.Result`? |
| Why `Task.WhenAll`? | Wait for all independent operations without serial awaiting startup. | 100 different-key test | Sequential acquisition hides concurrency defects. | Multiple faults; what does await throw? |
| Why completion sources? | Explicit control over when a fake SDK continues; async continuations avoid running arbitrary caller code inline. | same-key and race tests | Sleeps do not establish causal ordering. | Forget to signal; how bound a test hang? |
| Why `WaitAsync`? | Asynchronously cancel the wait, distinct from stopping the underlying task. | cancellation tests | Passing cancellation to underlying SDK has different ownership. | Wait abandoned; who observes later failure? |
| Why `ConcurrentDictionary`? | Atomic per-key map operations; factories may execute multiple times, so only the selected object owns work. | thread-pool contention test | Global locking serializes unrelated keys. | Side effects in factories; what changes with lazy tasks? |

### Branch 2 discussion updates

| Decision / question | Strong answer and code | Test | Alternative / cost | Failure / follow-up |
|---|---|---|---|---|
| Reclaim gates safely? | Replace gates with identity-removed lazy flights in `TokenService.Flight`. | `Shared_flights_are_reclaimed_after_success_failure_and_cancellation` | Ref-counted gates need careful ownership. | Removal races; why compare flight identity? |
| Who cancels shared work? | Service shutdown/deadline, never an individual waiter. | `Canceled_initiator_does_not_cancel_shared_acquisition` | Cancel when all waiters leave, with extra accounting. | Everybody leaves; why continue warming? |
| Can timeouts exceed SDK limit? | Physical operation retains its permit until it really exits. | `Uncooperative_sdk_keeps_physical_concurrency_slot_after_timeout` | Releasing at timeout can overcommit. | SDK never exits; can async disposal finish? |
| How bound key growth? | Admission bounds pending reservations; completed flights are removed. | `Sdk_concurrency_and_pending_key_capacity_are_bounded` | Unlimited queue improves admission but risks memory. | Capacity reached; what should caller retry? |
| How observe safely? | Fixed structured event labels, no exception payloads/context. | `Structured_metrics_use_fixed_events_not_tokens_or_contexts` | Per-account tags explode cardinality and expose data. | SDK secret in exception; why omit payload? |
| Half-open recovery? | One probe after time-based open interval in `L2Circuit`. | `Circuit_opens_skips_dependency_then_allows_one_recovery_probe` | Mature resilience library adds configurable policies. | Out-of-order completions; is health strongly ordered? |
| Which failures retry? | Only adapter-classified `TransientTokenException`, bounded attempts. | `Classified_transient_sdk_failure_retries_but_permanent_failure_does_not` | Provider-specific classifier adds coupling. | Auth failure; what makes retry safe? |

### Next changes

This heading interprets the truncated heading in the supplied task; it is not an extra product
requirement. Start with safe flight reclamation and shutdown, deadlines, bounded issuer concurrency,
and useful non-sensitive telemetry. Then add optional distributed fencing/version mechanisms only
where requirements justify the operational cost. Use a real cache adapter and integration tests
before making claims about a deployed distributed system. Verify SDK-specific identity semantics,
credential rotation, clock skew assumptions, and protected persistence with the application owner.

The branch progression is intentionally three different engineering questions, not a rewrite for
its own sake. Explain which newly required guarantee makes each added mechanism necessary.
