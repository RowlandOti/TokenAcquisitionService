# Observability

The library emits structured logs through injected `ILogger<TokenService>` and BCL metrics,
without configuring exporters or writing files. Meter: `TokenAcquisitionService`.

| Instrument | Unit / tag | Meaning |
|---|---|---|
| `token.events` | count / `event` | Fixed event names; no sensitive/high-cardinality tags |
| `token.sdk.duration` | milliseconds / none | Actual SDK attempt latency, excludes capacity wait/backoff |

Events: `l1_hit`, `l2_hit`, `miss`, `sdk_acquisition`, `sdk_failure`, `sdk_retry`, `sdk_timeout`,
`l2_failure`, `l2_timeout`, `fallback`, `lock_contention`, `capacity_throttle`, `cancellation`,
`freshness_rejection`, `circuit_open`, `circuit_half_open`, `circuit_closed`.
An acquisition timeout covers the whole fill, not proof the SDK itself caused the delay.

Logs use `Token acquisition event {Event}` with fixed labels. Dependency errors and fallback are
warnings; normal hits/joins are debug. SDK/provider exceptions propagate to callers but their
payloads are never passed to the logger because third-party exceptions may contain credentials.
Applications must also avoid unsafe logging of returned tokens or propagated exceptions.

`Structured_metrics_use_fixed_events_not_tokens_or_contexts` captures emitted measurements.
`Logs_never_include_sdk_exception_payload_or_access_token` injects secret-shaped payloads into
SDK and cache failures and verifies actual log output. Metric/log assertions are not a substitute
for configuring retention, access controls, or exporter redaction in a deployed application.

## Branch 3 distributed events

The same fixed-event counter/logger emits `lease_acquired`, `lease_contention`, `lease_expired`,
`lease_ownership_failure`, `lease_release_failure`, `conditional_write_rejected`,
`fencing_rejection`, `version_mismatch`, `version_store_failure`, `invalidation_sent`,
`invalidation_received`, `invalidation_recovery`, and `distributed_fallback`.
Lease expiry is observed when a late lease-backed write is rejected, not a proactive scheduler.
Distributed dependency failures share `version_store_failure` because interfaces must share the
same atomic authority domain; the event does not expose backend exception payloads.

`Distributed_events_and_logs_never_include_tokens_or_dependency_exception_payloads` captures
real emitted events/logs with injected secret-shaped token/provider payloads. Normal protocol tests
cover contention, fencing rejection, and recovery schedules. No key/account/tenant labels are added.
