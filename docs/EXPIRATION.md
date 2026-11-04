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

## Corresponding tests and limits

`Safety_boundary_is_strict`, `Empty_sdk_token_is_rejected_without_caching`,
`Token_expiring_during_L2_read_is_not_returned`,
`Token_expiring_during_write_is_not_returned_or_promoted`,
`L1_and_L2_ttls_are_capped_and_promotion_does_not_reset_L2_lifetime`,
`Near_expiry_L2_promotion_recalculates_usable_lifetime`.

Guarantee: fresh according to the supplied clock immediately before return. No guarantee:
valid at later downstream use, issuer revocation, unbounded clock jumps, or unsynchronized hosts.
No software can keep a returned token fresh forever; callers should use it promptly.
