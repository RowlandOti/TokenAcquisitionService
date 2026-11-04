# Branch comparison

| Dimension | Branch 1 | Branch 2 | Branch 3 |
|---|---|---|---|
| Engineering question | Is it correct? | Reliable under load/failure? | What can coordinating instances guarantee? |
| Identity / two-level cache-aside | Implemented | Preserved | Preserved, versioned envelopes optional |
| Absolute expiration | SDK expiry, buffer, caps | Deadline/failure paths hardened | Preserved on advanced path |
| Local singleflight | Retained keyed semaphore | Reclaimed shared task | Per-context/generation shared task |
| L2 fallback | Explicit best effort/strict | Timeouts/circuit, telemetry | Separate explicit authority policy |
| Cancellation | Active caller owns SDK lifetime | Service deadline/shutdown owns fill | Same plus bounded lease-release cleanup |
| Resource limits | Bounded L1, retained gates | Bounded pending keys/SDK calls, drain | Bounded local version cache, persistent authority metadata in simulation |
| Local races | Gate serialization | Shared-flight lifecycle stress | Also metadata-ordered L1 fills |
| Cross-instance races | Simulated limitation | Still possible | Optional leases/fenced writes |
| Conditional writes | Discussed | Discussed | Atomic simulated generation/fence/ownership check |
| Distributed leases | Discussed | Discussed | Optional leader/follower, expiry, safe release |
| Version stamps | Discussed | Discussed | Atomic invalidation, strict reads or optional local version leases |
| Notifications | Not required | Not required | Lossy hints, missed/duplicate/out-of-order tests |
| Observability | Fixed warnings | Structured events + SDK duration | Distributed fixed events |
| HybridCache | Not required | Not required | Actual pinned-package comparison tests |
| External infrastructure for tests | None | None | None |
| Exactly-once / issuer revocation | No | No | Still no |

Branch 3 does not require the advanced mode for every caller. The unconfigured service runs all
foundation and resilience tests unchanged except explicit robustness additions. The two-instance
legacy race test still passes: adding an optional protocol must not falsely improve claims about
uncoordinated deployments.

Each later branch descends from the completed previous branch. Foundation additions requested in
the expanded specification were committed additively, not by rewriting its original commit.
