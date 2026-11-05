# TokenAcquisitionService

A .NET 10 class library for acquiring tokens through a black-box SDK, with bounded-lifetime
L1 (`IMemoryCache`) and abstract L2 caching. No web host, identity provider, OAuth implementation,
Redis, or credentials are required. The tests are runnable examples.

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
The service owns L1 and gates, not the SDK, L2, logger, or clock.

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

## Branch progression

| Branch | Engineering question | Consistency |
|---|---|---|
| `feature/01-token-cache-foundation` | Is the implementation correct? | Local singleflight; L2 last-writer-wins |
| `feature/02-production-resilience` | Does it remain reliable under load and failure? | Same distributed contract; operational controls |
| `feature/03-distributed-coordination` | What guarantees can coordinating instances provide? | Optional leases, fenced writes, versions |

Each later branch is based on the completed preceding branch. Branch 1 intentionally exposes
cross-instance duplicate issuance and older-over-newer writes in deterministic tests.
There is no exactly-once or issuer-revocation guarantee.

Start with [architecture](docs/ARCHITECTURE.md) and the [interview guide](docs/INTERVIEW_GUIDE.md).

## Current branch: production resilience

Shared-task singleflight isolates caller cancellation. Service-owned timeouts/shutdown bound fills,
admission limits bound pending keys, and SDK permits remain held until actual completion.
L2 deadlines/circuit breaker and classified bounded SDK retries complement the same best-effort
or strict cache policy. Use async disposal to cancel and drain cooperative adapters.
See [production resilience](docs/PRODUCTION_RESILIENCE.md),
[observability](docs/OBSERVABILITY.md), and the [requirements matrix](docs/REQUIREMENTS_MATRIX.md).
These controls do not fix cross-instance last-writer-wins races.
