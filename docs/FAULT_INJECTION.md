# Fault injection and deterministic tests

All tests run without an issuer or distributed infrastructure. `FakeSdk` delegates behavior and
counts calls atomically; `CacheDouble` injects reads/writes; `FakeTimeProvider` drives TTLs,
deadlines, breaker intervals, and backoff. Completion sources establish ordering without sleeps.
Real-time ten-second waits and test-runner hang guards diagnose deadlocks, not correctness timing.

| Injected condition | Relevant test in `ResilienceTests` | Expected result |
|---|---|---|
| 500 distinct completed keys | `Shared_flights_are_reclaimed_after_success_failure_and_cancellation` | No retained completed flights |
| SDK saturation and pending admission | `Sdk_concurrency_and_pending_key_capacity_are_bounded` | Configured concurrency, explicit admission error |
| SDK waits until cancellation | `Acquisition_timeout_releases_flight_and_allows_recovery` | Timeout; next call succeeds |
| SDK ignores cancellation | `Uncooperative_sdk_keeps_physical_concurrency_slot_after_timeout` | No permit overcommit; completion recovers |
| L2 read never finishes | `L2_timeout_falls_back_then_recovers` | SDK fallback; restart reads successful write |
| Repeated L2 errors | `Circuit_opens_skips_dependency_then_allows_one_recovery_probe` | Open interval, one probe, recovery |
| Transient vs permanent SDK errors | `Classified_transient_sdk_failure_retries_but_permanent_failure_does_not` | Retry only explicitly classified error |
| Persistent transient error | `Retries_are_bounded_and_fake_clock_drives_backoff` | One retry then error |
| Shutdown during contention | `Shutdown_cancels_shared_work_and_rejects_new_calls` | Waiters cancel; new calls rejected |
| All callers cancel | `Canceling_every_waiter_still_allows_shared_cache_fill` | One fill still warms cache |
| Five waves of 200 calls across 10 keys | `Multiple_contention_waves_share_one_fill_per_key` | One fill per key, no retained flights |
| Metrics observation | `Structured_metrics_use_fixed_events_not_tokens_or_contexts` | Low-cardinality cache/acquisition events |

Foundation tests continue covering L2 exceptions, failed writes, strict fallback policy, invalid SDK
results, identity corruption, expiry during I/O, restarts, and deliberate cross-instance races.
The two slow-I/O freshness tests use longer operation deadlines so they exercise expiration rather
than the newly introduced timeout policy.

Run all tests with `dotnet test`. No test depends on network access, random delays, or real credentials.
The in-memory simulation does not establish a real Redis provider's atomicity, TTL, network-partition,
or serialization guarantees. Those need adapter contract and integration tests.
