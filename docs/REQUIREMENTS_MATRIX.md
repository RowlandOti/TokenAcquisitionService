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
| B1-DOC-01 / 1 | Five fundamentals, ten scenarios, >=30 questions, walkthrough | `docs/` | Manual documentation review; no documentation test framework | Documented | All foundation documents |
| B2-RES-01 / 2 | Reclaimed identity-safe shared flights, high cardinality, stress, recovery | `TokenService.cs` / `Flight`, `RunFlightAsync` | `ResilienceTests` / reclamation and contention wave tests; `OperationalTests` / repeated failure/recovery | Verified | `PRODUCTION_RESILIENCE.md` |
| B2-RES-02 / 2 | Pending admission, bounded physical SDK calls, no permit leaks | `TokenService.cs` / `AcquireAsync`, `InvokeSdkAsync` | `ResilienceTests` / capacity and uncooperative SDK tests; `OperationalTests` / capacity shutdown | Verified | `PRODUCTION_RESILIENCE.md` |
| B2-RES-03 / 2 | Separate read/write deadline overrides, fallback, circuit recovery | `Resilience.cs` / `L2Circuit.ExecuteAsync` | `ResilienceTests` / L2 timeout and half-open probe; `OperationalTests` / write timeout | Verified | `FAULT_INJECTION.md` |
| B2-RES-04 / 2 | Classified bounded retries, exponential backoff, optional jitter, deadline | `TokenService.cs` / `InvokeSdkAsync` | `ResilienceTests` / retry classification/budget; `OperationalTests` / exponential backoff and shutdown | Verified; jitter is optional nondeterministic load spreading | `PRODUCTION_RESILIENCE.md` |
| B2-CANCEL-01 / 2 | Caller-only wait cancellation, shared lifetime, shutdown/drain | `TokenService.cs` / `AcquireAsync`, `DisposeAsync` | `FoundationTests` / `Canceled_initiator_does_not_cancel_shared_acquisition`; `ResilienceTests` / all waiters cancel and shutdown | Verified intentional semantic evolution | `BRANCH_01_VS_02.md` |
| B2-OBS-01 / 2 | Fixed structured events, latency, no token/exception logs | `Resilience.cs` / `TokenTelemetry` | `ResilienceTests` / metrics test; `OperationalTests` / secret-payload logging test | Verified | `OBSERVABILITY.md` |
| B3-LEASE-01 / 3 | Lease, fencing, conditional write | Planned extension | Branch 1 demonstrates why needed | Documented limitation only | `RACE_CONDITIONS.md` |
| B3-VERSION-01 / 3 | Versions, invalidation, HybridCache comparison | Planned extension | Not present on branch 1 | Deferred to branch 3 | `INTERVIEW_GUIDE.md` |
