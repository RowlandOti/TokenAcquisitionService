# Branch 01 vs 02

| Concern | Foundation | Production resilience |
|---|---|---|
| Per-key synchronization | Retained semaphore | Reclaimed lazy shared task |
| Owner cancels | Owner SDK canceled; waiter retries | Owner only stops waiting |
| All callers cancel | No shared lifetime | Fill may continue within service deadline |
| Shutdown | Must drain before dispose | Cancellation plus async drain |
| Resource limits | L1 count only | L1, pending key reservations, actual SDK operations |
| L2 health | Configured fallback | Per-operation timeout and circuit breaker plus same fallback |
| SDK retry | None | Bounded, explicitly classified transient errors only |
| Telemetry | Fixed warning messages | Structured fixed events and meter |
| Distributed writes | Last-writer-wins | Unchanged |
| Cross-instance duplicate issuance | Possible | Still possible |

The SDK, cache, domain identity, expiration rules, and DI composition remain recognizable.
The owner-cancellation test changes intentionally: followers now share the same success/error,
instead of serially retrying a canceled owner's acquisition. A shared failure is removed; a later
call starts fresh. This avoids failure poisoning without making waiters themselves an automatic
retry framework.

Added complexity pays for a specific guarantee: service-owned deadlines/cancellation isolate
callers, activity accounting prevents dispose races, retained physical permits prevent overcommit,
and circuit controls limit outage amplification. None creates distributed ordering or revocation.
