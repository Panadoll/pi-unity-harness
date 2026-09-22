# Protocol fixtures

These JSON files are the machine-readable companion to `docs/protocol.md`.
Rust, C#, and TypeScript contract tests load this same directory.

## Layout

- `request/`: client-to-broker pipe request frames. Requests use `id`.
- `response/`: broker-to-client pipe response frames. Responses use `reply_to` and must not use `id` as an alias.
- `status/`: status, capabilities, and command-list result payloads.
- `mux/`: CLI mux stdout envelopes. Mux envelopes use `id` and must not use `reply_to` as an alias.
- `normalized/`: reserved for shared normalization tables when a table is clearer than an individual fixture.

Every fixture contains `description`, `direction`, `wire`, and `expect`. Unknown wire fields are ignored. A missing response `ok` normalizes to `malformed_response`; an `ok:true` envelope without `result` normalizes to `result:null`. A response without `reply_to`, or a mux envelope without `id`, is discarded and cannot complete an in-flight request.

Error classification precedence is `error_type`, then a closed-set broker `error` code, then `execution_failed`. `protocol_mismatch` is the only error type added by the architecture hardening work. Its error envelope carries `result: { expected, actual }`.

When adding or changing a fixture, update `docs/protocol.md` and all three contract-test manifests in the same change.
