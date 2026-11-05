# Requirements traceability

Branch 1 verification: `dotnet restore`, `dotnet build`, `dotnet test`; see Git commits for
persisted acceptance checkpoints. Status `verified` means the listed behavior was exercised by
the branch's executable tests, not proven for an external cache deployment.
Implementation paths are under `src/TokenAcquisitionService`; tests under
`tests/TokenAcquisitionService.Tests`. Groups below enumerate the supplied requirement families;
rows consolidate requirements exercised by the same behavior rather than duplicate assertions.

| ID / branch | Requirement | File / method | Test file / test | Status | Documentation |
|---|---|---|---|---|---|
| B1-MODEL-01 / 1 | Immutable requests, SDK expiry, app-only contexts, options validation | `Contracts.cs` / constructors | `AcceptanceTests` / `Application_only_and_delegated_contexts_remain_distinct`, `Invalid_expiration_options_are_rejected`; `FoundationTests` / canonical identity test | Verified | `DESIGN_DECISIONS.md` |
| B1-ID-01 / 1 | Tenant, client, principal, authority, flow, scope, parameter isolation | `Contracts.cs` / `CacheKey` | `FoundationTests` / `Security_context_is_isolated` theory | Verified | `CACHE_IDENTITY.md` |
| B1-ID-02 / 1 | Scope equivalence, immutable input, delimiter safety | `Contracts.cs` / constructor, `CacheKey` | `FoundationTests` / `Canonical_identity_is_order_independent_unambiguous_and_immutable` | Verified | `CACHE_IDENTITY.md` |
| B1-CACHE-01 / 1 | L1 hit, L2 hit/promotion, SDK miss, population | `TokenService.cs` / `AcquireAsync`, `WriteLocal` | `AcceptanceTests` / `L1_avoids_L2_and_sdk_and_L2_hit_repopulates_L1` | Verified | `ARCHITECTURE.md` |
| B1-CACHE-02 / 1 | Restart reuses L2 | `TokenService.cs` / constructor, `AcquireAsync` | `FoundationTests` / `Restart_rehydrates_L1_from_shared_L2_without_sdk` | Verified | `SCENARIOS.md` |
| B1-EXP-01 / 1 | Strict safety boundary and nonempty SDK result | `TokenService.cs` / `Validate` | `FoundationTests` / `Safety_boundary_is_strict`, `Empty_sdk_token_is_rejected_without_caching` | Verified | `EXPIRATION.md` |
| B1-EXP-02 / 1 | Independent caps, promotion recalculation | `TokenService.cs` / `Until`, `WriteLocal` | `FoundationTests` / TTL/promotion tests | Verified | `EXPIRATION.md` |
| B1-EXP-03 / 1 | Worked 10:00/10:40/2-minute buffer example | `TokenService.cs` / `Until`, `IsUsable` | `AcceptanceTests` / `Worked_1040_expiry_example_caps_promoted_L1` (seven times) | Verified | `EXPIRATION.md` |
| B1-EXP-04 / 1 | Expiry during read/write/SDK and after waiting | `TokenService.cs` / `Validate`, `IsUsable` | `FoundationTests` / expiring-during-read/write tests, `Expired_local_entry_is_rechecked_after_waiting`; `AcceptanceTests` / SDK expiry test | Verified | `EXPIRATION.md`, `RACE_CONDITIONS.md` |
| B1-CON-01 / 1 | 100 same-key callers, actual thread contention | `TokenService.cs` / per-key gate | `FoundationTests` / 100 same-key test; `AcceptanceTests` / `Actual_thread_pool_contention_preserves_singleflight` | Verified | `CONCURRENCY.md` |
| B1-CON-02 / 1 | 100 different keys overlap, tenant isolation | `TokenService.cs` / keyed coordination | `FoundationTests` / 100 different-key test; `AcceptanceTests` / `Parallel_tenants_receive_only_their_own_tokens` | Verified | `CONCURRENCY.md` |
| B1-CON-03 / 1 | Double-check after wait, external population | `TokenService.cs` / `AcquireAsync` | `FoundationTests` / `Canceled_owner_releases_gate_and_waiter_rechecks_external_population` | Verified | `CONCURRENCY.md` |
| B1-CANCEL-01 / 1 | Pre-cancel, waiting cancel, active cancel, no deadlock | `TokenService.cs` / `WaitAsync`, `finally` | `AcceptanceTests` / pre-canceled call; `FoundationTests` / cancellation tests | Verified | `FAILURE_BEHAVIOR.md` |
| B1-SDK-01 / 1 | Error propagation, 100 failing callers, recovery, no poisoning | `TokenService.cs` / SDK call, `finally` | `FoundationTests` / SDK failure test; `AcceptanceTests` / 100 failing callers, SDK failure during outage | Verified | `FAILURE_BEHAVIOR.md` |
| B1-FAIL-01 / 1 | L1 availability, read fallback, write best effort, strict policy | `TokenService.cs` / L2 exception filters | `FoundationTests` / outage, strict policy, L2 cancellation tests | Verified | `FAILURE_BEHAVIOR.md` |
| B1-RACE-01 / 1 | Local write order, freshness, same-key serialization | `TokenService.cs` / gate and final validation | `FoundationTests` / same-key/expiry tests | Verified | `RACE_CONDITIONS.md` |
| B1-RACE-02 / 1 | Two instances and older write wins | `InMemoryTokenCache.cs` / `SetAsync` | `FoundationTests` / `Two_instances_can_duplicate_acquisition_and_older_write_can_win` | Verified limitation, not prevention | `RACE_CONDITIONS.md` |
| B1-SEC-01 / 1 | Wrong-user envelope rejection and redacted diagnostics | `TokenService.cs` / `IsUsable`; `Contracts.cs` / `ToString` | `FoundationTests` / wrong-user envelope and diagnostic tests | Verified | `CACHE_IDENTITY.md` |
| B1-CACHE-03 / 1 | Reject mismatched L2 writes before mutation, preserve cancellation | `InMemoryTokenCache.cs` / `SetAsync` | `CacheBoundaryTests` / matching, mismatched, and canceled write tests | Verified | `CACHE_IDENTITY.md` |
| B1-EXP-05 / 1 | Consistent synchronous clock samples and final L1-hit validation | `TokenService.cs` / `IsUsable`, `WriteLocal`, `AcquireAsync` | `CacheBoundaryTests` / L1 boundary-crossing test; worked expiration theory | Verified | `EXPIRATION.md` |
| B1-DOC-01 / 1 | Five fundamentals, ten scenarios, >=30 questions, walkthrough | `docs/` | Manual documentation review; no documentation test framework | Documented | All foundation documents |
| B2-RES-01 / 2 | Reclaimed identity-safe shared flights, high cardinality, stress, recovery | `TokenService.cs` / `Flight`, `RunFlightAsync` | `ResilienceTests` / reclamation and contention wave tests; `OperationalTests` / repeated failure/recovery | Verified | `PRODUCTION_RESILIENCE.md` |
| B2-RES-02 / 2 | Pending admission, bounded physical SDK calls, no permit leaks | `TokenService.cs` / `AcquireAsync`, `InvokeSdkAsync` | `ResilienceTests` / capacity and uncooperative SDK tests; `OperationalTests` / capacity shutdown | Verified | `PRODUCTION_RESILIENCE.md` |
| B2-RES-03 / 2 | Separate read/write deadline overrides, fallback, circuit recovery | `Resilience.cs` / `L2Circuit.ExecuteAsync` | `ResilienceTests` / L2 timeout and half-open probe; `OperationalTests` / write timeout | Verified | `FAULT_INJECTION.md` |
| B2-RES-04 / 2 | Classified bounded retries, exponential backoff, optional jitter, deadline | `TokenService.cs` / `InvokeSdkAsync` | `ResilienceTests` / retry classification/budget; `OperationalTests` / exponential backoff and shutdown | Verified; jitter is optional nondeterministic load spreading | `PRODUCTION_RESILIENCE.md` |
| B2-CANCEL-01 / 2 | Caller-only wait cancellation, shared lifetime, shutdown/drain | `TokenService.cs` / `AcquireAsync`, `DisposeAsync` | `FoundationTests` / `Canceled_initiator_does_not_cancel_shared_acquisition`; `ResilienceTests` / all waiters cancel and shutdown | Verified intentional semantic evolution | `BRANCH_01_VS_02.md` |
| B2-OBS-01 / 2 | Fixed structured events, latency, no token/exception logs | `Resilience.cs` / `TokenTelemetry` | `ResilienceTests` / metrics test; `OperationalTests` / secret-payload logging test | Verified | `OBSERVABILITY.md` |
| B3-LEASE-01 / 3 | Leader/follower, recheck, healthy cross-instance coalescing | `DistributedPolicy.cs` / `FillAsync` | `DistributedTests` / `Two_instances_elect_leader_and_follower_rechecks_L2`; `InvalidationTests` / one-key multi-instance stress | Verified simulation | `DISTRIBUTED_COORDINATION.md` |
| B3-LEASE-02 / 3 | Expiry, stalled/failed leader, safe release, cancellation | `InMemoryDistributedStore.cs` / `TryAcquireAsync`, `ReleaseAsync`; `DistributedPolicy` / `finally` | `DistributedTests` / expired leader, failed leader, safe release, canceled follower; `InvalidationTests` / shutdown follower | Verified simulation | `DISTRIBUTED_COORDINATION.md` |
| B3-FENCE-01 / 3 | Monotonic atomic fences and stale/expired writer rejection | `InMemoryDistributedStore.cs` / `ReserveFenceAsync`, `TryWriteAsync` | `DistributedTests` / conditional out-of-order writes, concurrent fence allocation/writes, lease expiry before successor write | Verified simulation | `DISTRIBUTED_COORDINATION.md` |
| B3-FENCE-02 / 3 | Conditional write failure is explicit; leases optional | `DistributedPolicy.cs` / `FillAsync` | `DistributedTests` / conditional write failure; `InvalidationTests` / fenced writes without leases | Verified simulation | `CONSISTENCY_MODEL.md` |
| B3-VERSION-01 / 3 | Atomic generation increment and 5-versus-6 stale-fill race | `InMemoryDistributedStore.cs` / `InvalidateAsync`, `TryWriteAsync` | `DistributedTests` / atomic generations, exact generation-five race | Verified simulation | `VERSION_STAMPS.md` |
| B3-VERSION-02 / 3 | Missing metadata, unavailable/timed-out authority, explicit fallback | `DistributedPolicy.cs` / `GenerationAsync`, `CallAsync`; `TokenService` / fallback | `DistributedTests` / missing, unavailable, timeout, availability policy tests | Verified | `VERSION_STAMPS.md` |
| B3-CANCEL-01 / 3 | Shutdown also cancels authority lookup before any flight exists | `TokenService.cs` / `AcquireAsync` caller lifetime | `DistributedTests` / `Shutdown_cancels_version_lookup_before_a_shared_flight_exists` | Verified | `PRODUCTION_RESILIENCE.md` |
| B3-VERSION-03 / 3 | Optional local version lease, expiry, mismatch, stale window | `DistributedPolicy.cs` / `GenerationAsync` | `DistributedTests` / local lease staleness/recovery, valid lease during outage, disabled per-hit validation | Verified simulation, conditional observation bound | `VERSION_STAMPS.md` |
| B3-VERSION-04 / 3 | Delayed local fill cannot replace greater metadata | `TokenService.cs` / `WriteLocal` | `InvalidationTests` / `Delayed_old_local_fill_cannot_replace_newer_generation` | Verified | `DISTRIBUTED_COORDINATION.md` |
| B3-INVALIDATE-01 / 3 | Key invalidation, local eviction, notifications | `TokenService.cs` / `InvalidateAsync`, `EvictLocal`; bus / `PublishAsync` | `InvalidationTests` / delivered notification test | Verified simulation | `INVALIDATION.md` |
| B3-INVALIDATE-02 / 3 | Missed, duplicate, out-of-order, disconnected notification handling | `InMemoryInvalidationBus.cs`; `DistributedPolicy.cs` | `InvalidationTests` / duplicate/out-of-order, disconnected lease repair, TTL-only recovery | Verified simulation; not durable delivery | `INVALIDATION.md` |
| B3-SEC-01 / 3 | Multi-instance tenant isolation, restart, SDK failure | `TokenService.cs`, `DistributedPolicy.cs` | `DistributedTests` / parallel tenants, restart, failed leader | Verified | `DISTRIBUTED_SECURITY.md` |
| B3-SEC-02 / 3 | Secret-free distributed diagnostics, invalid/malformed results rejected | `TokenTelemetry`, `TokenService.Validate`, `DistributedPolicy.ReadAsync` | `DistributedObservabilityTests` / three diagnostic and malformed-result tests | Verified | `DISTRIBUTED_SECURITY.md` |
| B3-OBS-01 / 3 | Lease, fence rejection, mismatch, authority, invalidation, fallback events | `DistributedPolicy.cs` / telemetry calls | `DistributedObservabilityTests` / captured events; protocol tests trigger rejection/recovery | Verified | `OBSERVABILITY.md` |
| B3-HYBRID-01 / 3 | Official API/source investigation and runnable package comparison | Test-only package 10.0.0, `HybridCacheComparisonTests.cs` | Four tests: local stampede, cross-instance misses, serialized L2, one canceled waiter | Verified pinned package; no incomplete token adapter | `HYBRID_CACHE_COMPARISON.md` |
| B3-DOC-01 / 3 | Consistency, security assumptions, Redis Pub/Sub/durable alternatives | `docs/` | Manual review plus protocol tests | Documented; real adapters/deployment controls explicitly out of scope | `CONSISTENCY_MODEL.md`, `INVALIDATION.md`, `BRANCH_COMPARISON.md` |

### Historical references

The B1-CON-03 test is present on the foundation branch. Branch 2 intentionally replaces owner-driven
cancellation/gate retries with `Canceled_initiator_does_not_cancel_shared_acquisition` and tests
shared-flight cleanup. Branch 3's leader/follower test verifies L2 rechecking under distributed
contention. Earlier branch-specific methods are preserved in Git history, not falsely attributed
to the current shared-task implementation.

### Explicit limitations (not omitted features)

No real distributed-cache, issuer, broker, encryption service, or protected credential adapter is
implemented. Their abstractions/simulations satisfy the infrastructure-free scope, not deployment
readiness. Exactly-once issuance, issuer revocation, unbounded staleness guarantees under partitions,
and forced termination of uncooperative SDK code are not promised. HybridCache is evaluated with
actual package tests and official sources rather than shipping a partial alternative token adapter.
