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
