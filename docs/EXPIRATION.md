# B. Expiration

## Question and implementation

How do we avoid returning expired or nearly expired tokens? The SDK supplies `ExpiresAt`.
At a clock sample `now`, usable lifetime is `ExpiresAt - now - SafetyBuffer`.
It must be strictly positive. Each residence deadline is the earlier of
`ExpiresAt - SafetyBuffer` and `now + MaximumTtl`.

```csharp
var usableLifetime = token.ExpiresAt - timeProvider.GetUtcNow() - options.SafetyBuffer;
if (usableLifetime <= TimeSpan.Zero) throw new UnusableTokenException();
```

## Invariant and guarantees

No cache write extends SDK validity. Every cache read checks identity, residence deadline,
nonempty token, and remaining usable lifetime. L2 promotion computes a new L1 deadline bounded
by the remaining L2 deadline, the L1 cap, and token freshness. Acquisition results are checked
before persistence and again after asynchronous writes. At exactly the safety boundary, reject.

## Timeline

12:00 SDK says expiry 12:02; buffer 1 minute -> usable until 12:01.
12:00:50 L2 read starts -> 12:01 read completes -> reject and acquire anew.
If an L2 write consumes remaining usable lifetime, throw rather than returning that result.
Any already-persisted expired envelope will be rejected by future reads.

## Trade-offs and alternatives

A large safety buffer increases refresh frequency; a small one risks network/clock-skew failures.
Absolute expiry, not a repeatedly reset sliding TTL, protects validity. Earlier physical eviction
is acceptable. Deterministic logical deadlines use `TimeProvider`; `MemoryCache` also has its own
wall-clock eviction, which cannot make an expired logical entry usable.
Alternatives include refresh-ahead/jitter or SDK-managed cache expiration. Serving expired
tokens as stale-on-error is deliberately prohibited.

## Worked example (also an executable theory)

SDK expiry is 10:40, safety buffer two minutes, L1 maximum five minutes, L2 maximum sixty
minutes. The absolute last usable cutoff is 10:38 (exclusive), so initial L2 residence is
38 minutes, never sixty.

| Time | Remaining token life | Remaining usable life | Maximum new L1 TTL |
|---|---:|---:|---:|
| 10:00 | 40 min | 38 min | 5 min |
| 10:06 | 34 min | 32 min | 5 min |
| 10:20 | 20 min | 18 min | 5 min |
| 10:34 | 6 min | 4 min | 4 min |
| 10:37 | 3 min | 1 min | 1 min |
| 10:38 | 2 min | 0 min | 0 min |
| 10:40 | 0 min | 0 min (clamped) | 0 min |

`AcceptanceTests.Worked_1040_expiry_example_caps_promoted_L1` checks all seven points,
the initial L2 cutoff, and L1's last fresh tick/first expired tick. Fake time advances directly;
no wall-clock sleep determines correctness.

## Corresponding tests and limits

`Safety_boundary_is_strict`, `Empty_sdk_token_is_rejected_without_caching`,
`Token_expiring_during_L2_read_is_not_returned`,
`Token_expiring_during_write_is_not_returned_or_promoted`,
`L1_and_L2_ttls_are_capped_and_promotion_does_not_reset_L2_lifetime`,
`Near_expiry_L2_promotion_recalculates_usable_lifetime`.

Each synchronous envelope check and L1 deadline/TTL calculation uses one clock sample.
New samples are still taken after asynchronous I/O and at final token validation, including L1 hits.
`CacheBoundaryTests.L1_token_crossing_safety_boundary_before_final_validation_is_rejected`
verifies a token crossing the cutoff between lookup and final validation.

Guarantee: fresh according to the supplied clock at the final validation sample. No guarantee:
valid at later downstream use, issuer revocation, unbounded clock jumps, or unsynchronized hosts.
No software can keep a returned token fresh forever; callers should use it promptly.
