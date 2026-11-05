# A. Cache identity

## Question and selected implementation

Which attributes determine whether callers can share a token? `TokenRequest` snapshots tenant,
client, nullable principal, authority, authentication flow, scopes, and additional SDK parameters.
Scopes are distinct and ordinal-sorted; parameter names are ordinal-sorted. Canonical JSON of
these fields is SHA-256 hashed into a schema-versioned key (`tak:v1:`).

```csharp
var a = new TokenRequest("t", "app", "alice", "https://issuer", "delegated", ["b", "a", "a"]);
var b = new TokenRequest("t", "app", "alice", "https://issuer", "delegated", ["a", "b"]);
Debug.Assert(a.CacheKey == b.CacheKey);
```

## Invariant and guarantees

Non-equivalent supplied security contexts do not share entries (subject to SHA-256 collision
assumptions). Immutable snapshots prevent post-key mutation. JSON framing avoids delimiter
collisions; null principal is distinct from any account. Every cache read verifies embedded
`CacheKey` as well as expiry. No token, request, exception payload, or key is logged.

## Failure timeline

Alice requests K(A) -> storage mistakenly returns envelope K(B) -> reject -> SDK for Alice.
The mismatch does not promote Bob's token into Alice's L1.
The simulated L2 setter also rejects mismatched storage/envelope keys before mutation.
The wrong-user test injects a faulty read response to verify service-side defense independently.
Neither check authenticates maliciously relabeled opaque tokens.

## Trade-offs, alternatives, and limits

Conservative case-sensitive authority/tenant values may cause extra misses, never intentional
cross-context sharing. Authority URI normalization requires SDK-specific equivalence rules.
Scopes are treated as case-sensitive identifiers. Parameters must include all SDK knobs:
claims challenge, resource/audience, credential version, token binding, and so forth.
Secrets belong in the adapter's secure configuration, not the request/key. An adapter accepting
hidden identity-affecting options violates the contract.

An alternative is an SDK-provided cache key, or typed flow-specific requests with required fields.
HMAC keys could resist offline guessing of low-entropy identifiers; a plain hash is not encryption.
This implementation does not authenticate a malicious L2 payload or validate opaque token claims.
A trusted SDK/storage adapter remains necessary. An otherwise correctly labeled envelope containing
another user's opaque token cannot be detected without a stronger trusted binding.

## Corresponding tests

`Security_context_is_isolated` (seven dimensions),
`Canonical_identity_is_order_independent_unambiguous_and_immutable`,
`Another_users_envelope_is_rejected_even_under_the_requested_key`.
