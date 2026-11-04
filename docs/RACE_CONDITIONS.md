# D. Race conditions

**Branch 3 update:** the foundation limitations below still apply to the unconfigured path.
Optional [distributed coordination](DISTRIBUTED_COORDINATION.md) now implements conditional
generation/fence writes and ownership-safe leases. The original older-over-newer race test still
demonstrates the uncoordinated path; advanced tests assert stale-write rejection separately.

## Question and implementation

How do concurrent acquisitions and writes affect cached values? A per-key gate serializes local
cache fills. The cache is rechecked after ownership is acquired. L2 uses unconditional,
atomic per-entry replacement, **not** version comparison.

```csharp
// Branch 1 L2 simulation: an atomic replacement, not a distributed ordering guarantee.
entries[key] = envelope;
```

## Invariant and consistency contract

Every returned token must match the requested envelope key and pass freshness checks.
Writes never extend token validity. A single service cannot reorder its own same-key fills.
Two service instances may independently observe misses and acquire duplicate tokens.
L2 is last-writer-wins; L1 copies may diverge until their residence deadlines.
Cache residence ordering and token issuance ordering are different concepts.

## Cross-instance timeline

1. A misses and starts an acquisition that will return an older token.
2. B misses and acquires a newer token; B writes L2 and B's L1.
3. A completes late; its still-fresh older token replaces L2 and fills A's L1.
4. B continues serving its newer local token; a restarted C reads the older L2 token.

This is allowed in Branch 1. Later expiration is **not** proof of greater authority or issuance
order. Only the issuer/coordination protocol can provide trustworthy ordering metadata.

## Trade-offs and alternatives

Local semaphores cannot coordinate processes. Global process locks would unnecessarily serialize
different keys and still not solve distributed races. Conditional writes alone require a
trustworthy generation. Distributed leases reduce duplicates but lease expiry permits overlap.
Branch 3 combines fencing/atomic conditional writes with optional version validation; an expired
leader must not overwrite an accepted newer generation. Version advancement and writes must be
atomic at the authority, not a read-then-write sequence in the client.

## Corresponding tests and limits

`Two_instances_can_duplicate_acquisition_and_older_write_can_win` deterministically pauses A,
lets B write, then releases A. It asserts the limitation, not an imagined distributed guarantee.
`Canceled_owner_releases_gate_and_waiter_rechecks_external_population` tests double-checking.
`Token_expiring_during_write_is_not_returned_or_promoted` protects delayed results.
`Restart_rehydrates_L1_from_shared_L2_without_sdk` demonstrates process-local state loss.

No guarantees of exactly-once acquisition, distributed invalidation, issuer revocation, or
linearizable L1 reads are made in this branch.
