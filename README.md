# TokenAcquisitionService

A .NET 10 class library for acquiring tokens through a black-box SDK, with bounded-lifetime
L1 (`IMemoryCache`) and abstract L2 caching. No web host, identity provider, OAuth implementation,
Redis, or credentials are required. The tests are runnable examples.

Private repository: https://github.com/RowlandOti/TokenAcquisitionService.
The default branch contains the final implementation; the three feature branches preserve the
engineering progression. See [delivery report](docs/DELIVERY_REPORT.md) for validation and draft
comparison pull requests.

## Run

Install the supported .NET 10 SDK, then:

```sh
dotnet restore TokenAcquisitionService.sln
dotnet build TokenAcquisitionService.sln --no-restore
dotnet test TokenAcquisitionService.sln --no-build
```

On macOS prefix long-running commands with `caffeinate -dimsu`. In the development workspace
the SDK is installed at `.tools/dotnet/dotnet` (ignored by Git); substitute that path for `dotnet`
if the machine's global SDK is older.

## Source layout and packaging

The solution ships one library, not a separate assembly for each folder:

```text
src/TokenAcquisitionService/
  Abstractions/          SDK, service, and cache interfaces
    Distributed/         Version, lease, conditional-cache, and notification interfaces
  Models/                Immutable acquisition request and result
  Caching/               Cache envelope and in-memory L2 simulation
  Options/               Cache configuration and validation
  Exceptions/            Public exception types
  DependencyInjection/   Service registration
  Resilience/            L2 deadlines and circuit breaker
  Diagnostics/           Metrics and fixed-payload logging
  Distributed/           Coordination policy, configuration, and in-memory simulations
    Models/              Lease grant and invalidation message
  TokenService.cs        Acquisition orchestration and lifecycle
tests/TokenAcquisitionService.Tests/
```

Each top-level production type has its own file. Folders organize responsibilities while
public types retain the `TokenAcquisitionService` namespace for source/binary compatibility.
Later branches add resilience, diagnostics, and distributed-coordination implementation folders.
Closely coupled private nested implementation types may stay with their owning class.

Build a local NuGet package (no publishing is performed):

```sh
dotnet pack src/TokenAcquisitionService/TokenAcquisitionService.csproj -c Release
```

The package includes the library, XML documentation, and this README, but not the local-only
`docs/*.md` files. Keep one package until independent consumers or adapter dependencies justify
separate packages; folders alone do not justify additional assemblies.

## Integration

```csharp
services.AddSingleton<ITokenSdk, YourSdkAdapter>();
services.AddSingleton<ITokenCache, YourDistributedCacheAdapter>();
services.AddTokenAcquisitionService(new TokenCacheOptions
{
    SafetyBuffer = TimeSpan.FromMinutes(1),
    BestEffortL2 = true
});
var service = provider.GetRequiredService<ITokenService>();
var result = await service.AcquireAsync(new TokenRequest(
    "tenant", "application", "account", "https://issuer.example/tenant",
    "delegated", ["api.read"]), cancellationToken);
```

`YourSdkAdapter` and `YourDistributedCacheAdapter` are application-owned types, not shipped
placeholders. `InMemoryTokenCache` is the included thread-safe simulation; register a shared
instance to simulate separate services. Register the token service as a singleton per process.
The service owns L1, shared flights, shutdown, and SDK permits, not the SDK, L2, logger, or clock.

## Assumptions and security boundary

- The SDK adapter returns the token for exactly the requested context. Callers must supply every
  identity-affecting SDK parameter (audience/resource, claims, credential identity/version,
  proof-of-possession binding, etc.) through the request. Do not put credential secrets in it.
- Authority, tenant, account, flow, and scope casing are preserved conservatively. Scope order and
  duplicates are normalized; URI aliases are not guessed to be equivalent.
- L2 is trusted storage with access controls and encryption chosen by the real adapter. The
  envelope detects a wrongly routed entry, not malicious substitution of both token and metadata.
  Tokens are opaque: this library cannot verify issuer claims or revocation.
- Clock synchronization and a safety buffer are required across hosts. Freshness is checked
  at the final validation sample; a token can expire before caller observation or later use.
- The SDK must cooperate with cancellation. Credentials, token persistence protection, eviction,
  and production distributed infrastructure are deliberately outside this exercise.

## Design decisions and trade-offs (Branch 01)

| Decision | Why | Trade-off |
|---|---|---|
| Cache-aside | The SDK remains the authoritative token issuer; the service checks caches, acquires on a miss, and explicitly populates L2 then L1. | Acquisition and cache writes are not atomic. Cache writes do not issue tokens, so write-through/write-back do not fit this boundary; write-around would sacrifice reuse. |
| L1 plus shared L2 | L1 avoids network calls; L2 allows reuse across service instances. | Two cache layers require coordinated expiration, and L1 can temporarily differ between instances. |
| Immutable context and canonical SHA-256 keys | Snapshot every identity-affecting input, normalize scope order/duplicates, and avoid exposing raw context in keys. | Isolation depends on complete inputs and correct canonicalization. Hashing is not encryption or proof that a cached token is authentic. |
| Safety buffer and bounded TTLs | Reject nearly expired tokens; cap L2 residence and cap L1 against both its own maximum and L2's `CacheUntil`. | Earlier refreshes increase SDK calls. Freshness holds at the final validation sample, not throughout subsequent caller use. |
| Per-key asynchronous gates with a cache recheck | Same-key callers serialize without blocking thread-pool threads; waiters reuse a successful fill while unrelated keys proceed independently. | Gates remain allocated until disposal. Failures are not shared results, so later waiters can attempt acquisition again. Coordination is local to one service instance. |
| Best-effort L2 by default, with strict mode available | A shared-cache failure need not prevent returning a valid SDK-issued token; failures are logged, while cancellation propagates. | An outage reduces shared reuse and can increase SDK load. Strict mode instead propagates L2 failures. |
| Deterministic fake SDK, cache, and clock | Reproduce expiration boundaries, contention, and distributed races without sleep-based timing. | Unit tests demonstrate the library contract, not real adapter serialization, network behavior, or production infrastructure. |

## Possible improvements and interview extensions

These are not implemented in Branch 01:

1. **Coordinated disposal during active operations:** stop accepting requests, signal shutdown
   cancellation, and drain admitted operations before disposing L1 and semaphores. Add asynchronous
   disposal so callers can await cleanup. This prevents resource-use/release races; draining can
   still wait indefinitely if an SDK or cache dependency ignores cancellation.
2. **Operational resilience:** add acquisition/L2 deadlines, bounded pending work and physical SDK
   concurrency, classified retries with backoff, circuit breaking, and metrics. These controls
   bound load and improve failure visibility, but add configuration and state-machine complexity.
   A deadline cannot forcibly stop an uncooperative SDK.
3. **Cross-instance coordination:** add per-key distributed leases, atomic fenced writes, and
   generation/version checks for invalidation. These reduce duplicate issuance and reject stale
   publication, but require shared-store atomicity and an explicit coordination-failure policy.
   Lease expiry still permits duplicate SDK calls; exactly-once issuance requires issuer-side support.

Branches 02 and 03 explore these extensions rather than changing Branch 01's foundation contract.

## Branch progression

| Branch | Engineering question | Consistency |
|---|---|---|
| `feature/01-token-cache-foundation` | Is the implementation correct? | Local singleflight; L2 last-writer-wins |
| `feature/02-production-resilience` | Does it remain reliable under load and failure? | Same distributed contract; operational controls |
| `feature/03-distributed-coordination` | What guarantees can coordinating instances provide? | Optional leases, fenced writes, versions |

Each later branch is based on the completed preceding branch. Branch 1 intentionally exposes
cross-instance duplicate issuance and older-over-newer writes in deterministic tests.
There is no exactly-once or issuer-revocation guarantee.
## Production resilience (preserved in Branch 3)

Shared-task singleflight isolates caller cancellation. Service-owned timeouts/shutdown bound fills,
admission limits bound pending keys, and SDK permits remain held until actual completion.
L2 deadlines/circuit breaker and classified bounded SDK retries complement the same best-effort
or strict cache policy. Use async disposal to cancel and drain cooperative adapters.
Detailed walkthroughs under `docs/` are local-only and are not included in clones or packages.
These controls do not fix cross-instance last-writer-wins races.

## Optional distributed coordination (Branch 3)

Pass `DistributedConfiguration` to opt into atomic generation/fence writes, optional leader
leases, optional version-validated reads/local version leases, and optional lossy invalidation
notifications. The ordinary path does not perform distributed version lookups.
See [integration example and protocol](docs/DISTRIBUTED_COORDINATION.md),
[consistency guarantees](docs/CONSISTENCY_MODEL.md), [version policies](docs/VERSION_STAMPS.md),
and [HybridCache evidence](docs/HYBRID_CACHE_COMPARISON.md).

The in-memory distributed store/bus are explicitly demonstration adapters. No Redis, issuer,
Docker, web application, or real credentials are needed to run tests. Production adapters must
implement the same atomic authority operations and protect stored tokens.
