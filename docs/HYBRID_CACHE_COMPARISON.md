# HybridCache comparison

This is a focused **executable comparison**, not an alternative token adapter advertised as complete.
The original implementation remains intact. `Microsoft.Extensions.Caching.Hybrid` **10.0.0** is
pinned in the test project only; it is not a production library dependency.

## Evidence consulted

- Official guide: https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0
- API reference: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.caching.hybrid.hybridcache
- Pinned implementation (10.0.0): https://github.com/dotnet/extensions/blob/v10.0.0/src/Libraries/Microsoft.Extensions.Caching.Hybrid/Internal/DefaultHybridCache.cs

The API reference's default view can show previews; claims here target the compiled 10.0.0 package
and executable tests, not preview overloads. Source inspection confirms exact `MemoryDistributedCache`
plus `MemoryCache` is optimized into an L1-only setup. Comparison tests therefore use a distinct
shared `IDistributedCache` wrapper to exercise genuine serialized L2 reads/writes across providers.

| Concern | HybridCache 10 | Custom service |
|---|---|---|
| L1/L2 | Primary local cache, optional `IDistributedCache` | Owned bounded L1, required L2 abstraction |
| Local stampede | Shared factory per key/instance | Shared task per context/generation/service |
| Different instances | Shared L2 does not coalesce factories | Optional leases reduce cross-instance duplicates |
| Serialization | Built-in string/bytes; default JSON for other types, extensible serializers | Typed abstraction; real adapter supplies protected serialization |
| Expiration | `Expiration` and `LocalCacheExpiration` entry options | Absolute SDK expiry, safety buffer, TTL caps, promotion recalculation |
| Cancellation | One canceled caller does not cancel other waiters; factory token cancels when all participating cancel | Shared fill survives all caller cancellation until deadline/shutdown |
| Remove by key | Removes that instance's L1 and L2 entry | Optional generation invalidation, local eviction and notifications |
| Tags | Logical invalidation checked during reads, not physical immediate deletion everywhere | Explicit generation authority with configurable read validation |
| Fencing | No application-specific fenced issuer write protocol | Atomic generation/fence/ownership checks in optional protocol |
| Revocation | None | None |

`RemoveAsync` source removes the invoking instance's `_localCache` and shared backend key; it does
not broadcast eviction into every other process's L1. Tag invalidation is logical and does not
make physical local copies vanish simultaneously. Neither is evidence of exactly-once issuance,
durable Pub/Sub, distributed fencing, or an atomic transaction spanning token return/use.

## Executable comparisons

`HybridCacheComparisonTests` verifies:

1. 100 same-instance callers execute one factory.
2. Two instances sharing real L2 serialization still execute two overlapping miss factories.
3. A new instance reads a serialized `TokenResult` from shared L2 without invoking its factory.
4. Canceling one participant leaves another waiter able to receive the shared result.

The dependency's all-callers-cancel behavior is documented by the official guide; it is not inferred
from the single-canceled-waiter test. Tests use controlled completion sources, not sleeps.

## What token-specific work remains?

Canonical security-context keys, SDK trust, absolute-expiry validation on **every returned value**,
safety buffers, unusable factory-result rejection, promotion behavior, and explicit failure/cancellation
policies are still needed. `GetOrCreateAsync` cache options are supplied before the factory returns;
blindly applying a configured lifetime can outlive an SDK result with unexpectedly short validity.
An adapter would need to solve dynamic expiry and stale-hit validation rather than simply wrap
`GetOrCreateAsync<TokenResult>`. Distributed fencing/version requirements remain separate.

HybridCache is attractive if its simpler cache-coalescing contract is sufficient. For this exercise,
comparison tests are a safer scope choice than shipping an incomplete token adapter.
