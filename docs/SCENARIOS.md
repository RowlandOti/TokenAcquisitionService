# Ten interview scenarios

Implementation methods are in `TokenService.cs`; tests in `FoundationTests.cs` unless indicated.
All scenarios assume trustworthy SDK context mapping, shared storage, and clock.

| # / problem and cause | Implementation / test | Expected result | Trade-off / limitation | Follow-up |
|---|---|---|---|---|
| 1. 100 same-key misses race before caching | `AcquireAsync` per-key gate; `One_hundred_same_key_callers_use_one_acquisition` | One successful SDK call | Failed owners may cause retry; local scope only | Shared task vs semaphore? |
| 2. 100 independent contexts need tokens | Dictionary keyed gates; `One_hundred_different_keys_acquire_independently` | Calls overlap | Unbounded issuer concurrency in foundation | How impose capacity? |
| 3. SDK throws while waiters queue | `finally` release; `Sdk_failure_does_not_poison_cache_or_gate` and `AcceptanceTests.One_hundred_failing_callers_are_released_and_later_recovery_succeeds` | Exceptions propagate, recovery works | No retry policy | Which failures are transient? |
| 4. Caller abandons request | `WaitAsync` and propagated token; cancellation tests | Canceled wait exits, owner releases gate | Owner cancellation can force waiter retry | Who owns shared lifetime? |
| 5. L2 unavailable | Explicit catch filter / best-effort flag; outage and strict-policy tests | L1 survives, configured SDK fallback | Increased issuer load, no breaker yet | Strict vs availability policy? |
| 6. Token expires during I/O | `Validate` / `IsUsable`; expiry-during-read/write and `AcceptanceTests.Sdk_result_expiring_during_acquisition_is_rejected` | Never return an unusable token | Acquisition can fail instead of silently retry | How handle clock skew? |
| 7. Two instances miss together | Independent L1/gates, shared L2; two-instance race test | Duplicate issuance possible | Local gate is not distributed lock | Does a lease give exactly once? |
| 8. Old acquisition finishes late | Unconditional `SetAsync`; same race test | Older still-fresh token can replace L2 | No trustworthy issuance ordering | How do fenced writes help? |
| 9. Storage routes another user's entry | Embedded identity check; wrong-user envelope test | Reject mismatch, acquire correct context | Cannot verify malicious opaque token relabeling | Authenticated envelope or SDK binding? |
| 10. Process restarts | New L1 and gates; restart test | Shared fresh L2 reused | Crashed process does not retain L1 | Still-running disconnected process instead? |

The two-instance race test controls the ordering with completion sources: A starts, B writes,
then A finishes. It explicitly demonstrates both scenarios 7 and 8 without claiming a distributed
guarantee. Every scenario has a failure or cost worth discussing, rather than a blanket “cache works.”
