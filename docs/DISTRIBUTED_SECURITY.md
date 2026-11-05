# Distributed security boundary

The library does not implement identity issuance, JWT parsing/signing, transport encryption,
credential storage, or a cache server. These remain application/adapter responsibilities.

| Concern | Current protection | Required deployment control |
|---|---|---|
| Context isolation | Canonical immutable request + embedded context key | SDK must honor all request fields; include audience/claims/binding/credential version |
| Cache confidentiality | No token/context/exception payload logs; redacted token diagnostics | TLS, cache ACLs, isolated namespaces, encryption at rest where applicable |
| Serialization | Immutable typed envelopes; key/freshness/version checks | Bounded payloads and known schema/types; reject malformed input, no arbitrary type activation |
| Atomic authority | Per-key simulation transaction for versions/fences/write | Real server-side atomic conditional operation, durable monotonic metadata |
| Invalidation | Epoch advancement then hints | Authenticate/authorize publishers and invalidators; rate-limit abuse |
| Version authority outage | Explicit fail-closed or fresh uncached SDK policy | Choose policy consistent with security/availability requirements |
| Namespace | Schema-prefixed hashed security context | Isolate environment/application/protocol namespaces and writer credentials |
| Diagnostics | Fixed low-cardinality events only | Protect exporters/log access and retention; callers must not log SDK secrets |
| Revocation | None claimed | Issuer/resource server enforcement |

SHA-256 keys are not encrypted identity data; low-entropy identifiers can be guessed offline.
Do not pass client secrets in `TokenRequest.Parameters`, even though the key is hashed.
Use SDK-owned credential configuration and an identity/version reference for key differentiation.

A wrong-key envelope is rejected. An attacker able to replace a token **and** forge its matching
envelope can bypass this routing check; opaque access tokens do not provide a verifiable binding
to this library. Trust/access-control of L2 is an explicit assumption, not a security implementation.

Do not mix legacy unconditional writers into the coordinated namespace. The demonstration store
rejects unconditional `SetAsync`; a real adapter should expose only authorized atomic operations.
Counters must survive token TTL and authority failover without rollback. Do not deploy the in-memory
simulations as distributed infrastructure.

Tests: identity isolation/wrong-user envelope tests, multi-instance tenant isolation,
`Distributed_events_and_logs_never_include_tokens_or_dependency_exception_payloads`,
`Null_sdk_result_is_rejected_as_unusable_without_caching`, and malformed envelope rejection.
