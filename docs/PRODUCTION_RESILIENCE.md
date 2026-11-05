# Production resilience (Branch 2)

Branch 2 changes operational behavior, not distributed consistency. It remains a compact interview
implementation, not a production-complete package.

## Shared-task singleflight and resource ownership

`TokenService.Flight` owns a lazy shared task per key. `ConcurrentDictionary.GetOrAdd` selects
one flight; losing candidates never start. Completion removes the **same flight identity** rather
than blindly removing a potentially newer task. Entries are reclaimed on success, failure, and
shutdown. No global acquisition lock or retained per-key semaphore exists.

Every caller awaits with its own cancellation token. Canceling even the initiating caller does
not cancel shared work. Shared work has service-shutdown and acquisition-deadline cancellation.
If every waiter cancels, work still finishes and warms caches, subject to that deadline.

`MaximumPendingKeys` bounds distinct pending key reservations (default 4096); admission failure
throws `AcquisitionCapacityException`, not an empty token. At capacity, simultaneous initial
reservations can reject a contender before another has published its same-key flight; callers may
retry admission. `MaximumConcurrentSdkCalls` bounds actual in-flight SDK operations (default 128),
including retries, across keys. The permit remains held until an uncooperative SDK actually completes,
even if callers already timed out. This prevents a timeout storm from exceeding physical concurrency.
Queueing consumes the same overall acquisition deadline.

An activity reference count separates public waits, shared fills, SDK operations, and the owning
service reference. `Dispose` rejects new calls and signals shutdown; `DisposeAsync` also waits for
draining before releasing owned L1, semaphore, cancellation source, and meter. Caller cancellation
cannot dispose resources still used by a flight. External adapters are never disposed by this service.

## Timeouts, retries, and circuit breaker

- `AcquisitionTimeout` bounds the shared fill (default 30 seconds).
- `L2Timeout` bounds each read/write (default 2 seconds).
- `L2ReadTimeout` and `L2WriteTimeout` optionally override the common deadline independently.
- All timers/backoff use `TimeProvider`, not sleeps.
- Three consecutive L2 operation failures open a per-service circuit for 15 seconds by default.
  Requests fail fast while open. One half-open probe is admitted after the interval; other calls
  follow fallback policy. A successful operation closes/reset the circuit; a failed probe reopens.
- Reads and writes share the circuit. Counters reflect operations, not failed logical requests.
  Concurrent operations can complete out of order; this is a lightweight operational heuristic,
  not a strongly ordered dependency health authority.
- Caller/shutdown cancellation is never counted as dependency failure. A dependency's independent
  `OperationCanceledException` also propagates rather than becoming an apparently successful miss.
- Only `TransientTokenException`, deliberately classified by the SDK adapter, is retried.
  Default one retry, maximum five, cancellable exponential backoff capped by `MaximumRetryDelay`.
  `UseRetryJitter` optionally multiplies each delay by a random factor in [0.5, 1.5), then applies
  the cap; disabled by default for reproducibility. Permanent authentication errors
  propagate on the first attempt. L2 is not retried inline: fallback/circuit controls amplification.

SDK retries can issue extra tokens and hit rate limits; only an adapter that knows the provider's
failure semantics should classify them. There is no exactly-once effect. Capacity admission is
not FIFO; `SemaphoreSlim` does not promise strict fairness, so hot workloads may delay other keys.
The total deadline bounds a queued caller's wait, not starvation-free scheduling.

Storage read/write failures and timeouts obey `BestEffortL2`. Strict mode propagates them.
No stale-on-error serving is introduced.

## Observability

`TokenTelemetry` emits structured logs with a fixed `{Event}` property and a
`System.Diagnostics.Metrics` counter: meter `TokenAcquisitionService`, instrument `token.events`,
tag `event`. Values include `l1_hit`, `l2_hit`, `miss`, `sdk_acquisition`, `sdk_failure`,
`l2_failure`, `fallback`, `lock_contention`, `freshness_rejection`, `cancellation`, `sdk_timeout`.
Warnings identify fallback/dependency failures; routine cache events are debug-level.
Lock contention means joining an already-started shared flight, not time spent blocking a thread.

No token, raw key, account, tenant, request, or exception payload is logged or used as a metric tag.
Consumers can attach their logging provider and `MeterListener`/OpenTelemetry exporter; no exporter
or background infrastructure is required.

## Guarantees and limits

Deterministic tests cover deadline/recovery, cancellation isolation, shutdown, physical concurrency,
admission bounds, circuit recovery, retry classification, and repeated contention waves.

The service cannot forcibly stop arbitrary SDK/storage code. Timeout ends the wait, not the
external side effect. An ignored cache cancellation may still write later (Branch 1 distributed
race semantics remain). An SDK ignoring cancellation retains its bounded slot and may prevent
`DisposeAsync` from draining forever. A process boundary is needed to forcibly isolate truly
uncooperative code. There is deliberately no claim of bounded termination of arbitrary adapters.
Choose limits for the deployment; a fixed limit of 128 is illustrative, not performance tuning.
