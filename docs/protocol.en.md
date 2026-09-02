# pi-unity-harness Communication Protocol Specification (v1)

> This document is the **single source of truth** for the pi-unity-harness client ↔ Unity process communication protocol.
> Reference implementations: `native/src/lib.rs` (Rust native broker), `unity/com.pi.unity-harness/Editor/PiUnityBridge.cs` (C# managed worker), `native/src/bin/pi_unity.rs` (native CLI client), `.pi/extensions/pi-unity-harness/index.ts` (pi thin-wrapper extension).
> Any implementation (CLI, pi extension, client in another language) MUST follow this document and MUST NOT extend or change field semantics on its own.


## 1. Overall architecture

```text
Client (any language)               Unity Editor process
  |  named pipe, JSON line             |
  |  ------------------------------->  |  Rust native broker (cdylib)
  |                                    |    - pipe server, survives domain reload
  |                                    |    - auth, queuing, timeout, audit, state plane
  |                                    |  C# managed worker
  |                                    |    - executes on EditorApplication.update (main thread)
  |  <-------------------------------  |    - eval / compile / snapshot / pipeline
```

- Transport: Windows Named Pipe, byte mode, UTF-8, JSON lines (`\n`-delimited, one message per line)
- Lifecycle: the native broker starts with the Unity process; the pipe keeps its name and the connection survives domain reload
- Single-client semantics: the pipe serves exactly one client connection at a time; on disconnect all pending requests fail with `client_disconnected`
- Client discovery entry point: `<UnityProject>/Library/PiUnityHarness/bridge.json`

## 2. bridge.json (client discovery)

Written at `<UnityProject>/Library/PiUnityHarness/bridge.json` (UTF-8, possibly with BOM) after the Editor starts and the broker initializes.

```json
{
  "project": "/path/to/UnityProject",
  "pid": 12345,
  "pipe": "\\\\.\\pipe\\pi_unity_<sha256-first-6-bytes-hex>",
  "token": "0123456789abcdef0123456789abcdef",
  "generation": 3,
  "statePlaneName": "Local\\PiUnityHarnessState_<hash>"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `project` | string | Absolute path of the Unity project |
| `pid` | number | Unity Editor process ID |
| `pipe` | string | Full pipe path (with `\\.\pipe\` prefix); clients can pass it directly to `net.createConnection(pipe)` |
| `token` | string | Auth token, 32 hex chars; persisted by the Editor session (stable across domain reloads) |
| `generation` | number | Managed domain generation; incremented by 1 on every domain reload |
| `statePlaneName` | string | Shared-memory state plane name (`Local\` prefix); degraded state source when the pipe is unreachable |

The pipe name is `pi_unity_` + the first 6 SHA-256 bytes (12 hex chars) of the project path, while `statePlaneName` is derived from a 64-bit FNV-1a hash of the normalized project path. Both are stable across launches of the same project. Clients MUST use the `pipe` field from `bridge.json` verbatim and MUST NOT re-derive the name.

## 3. Frame format

### 3.1 Request frame (Client → Broker)

```json
{
  "id": "req-1",
  "type": "execute_code",
  "token": "0123...",
  "timeoutMs": 20000,
  "payload": { "...": "..." }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `id` | yes | Request ID, non-empty; responses refer back via `reply_to`. A uuid is recommended |
| `type` | yes | Request type, see section 4 |
| `token` | yes | MUST equal the bridge.json token, otherwise the response is `unauthorized` |
| `timeoutMs` | no | Client-expected timeout budget (ms). The native-side actual timeout is `requested + 5000`, clamped to `[1000, 600000]`. Default 60000 |
| `payload` | depends | Per-type arguments, see section 4 |

### 3.2 Response frame (Broker → Client)

Success:

```json
{"reply_to": "req-1", "ok": true, "result": { "...": "..." }}
```

Failure:

```json
{"reply_to": "req-1", "ok": false, "error": "unauthorized", "error_type": "auth"}
```

| Field | Description |
|-------|-------------|
| `reply_to` | The request ID being answered |
| `ok` | Success flag |
| `result` | Success payload (object) |
| `error` | Error code or error message (string) |
| `error_type` | Optional; error classification added for managed-side failures (see 5.2) |

### 3.3 Event frame (Broker → Client, one-way)

```json
{"type": "event", "event": "some_event", "payload": "..."}
```

Clients MUST ignore unknown events. The broker currently reserves this frame type (C# side `pi_unity_emit_event`); no events are published yet.

### 3.4 Transport-level rules

- Requests and responses are single-line JSON ending with `\n`; unescaped newlines inside a line are not allowed
- The maximum request frame size is 1 MiB (`REQUEST_BUFFER_LIMIT`); larger frames get `request_too_large`
- When a client is idle for 15 seconds with no in-flight request, the broker disconnects it (`CLIENT_HEARTBEAT_TIMEOUT_MS`)
- Clients are advised to send a `ping` every 5 seconds while idle (reference-client behavior); the broker does not enforce ping but does disconnect after 15 seconds of silence
- After disconnecting, a client may simply reconnect; in-flight requests are not resumed and must be retried by the client

## 4. Request types

### 4.1 Handled directly by the broker (no managed dependency, no domain-reload coupling)

| type | payload | result | description |
|------|---------|--------|-------------|
| `ping` | none | `{"pong": true}` | keepalive / connectivity probe |
| `status` | none | status payload (see 6.1) | broker-level status; does not require managed ready |
| `timeline` | `{limit?, requestType?, action?, success?}` | audit query result (see 7) | `limit` 1–200, default 50; `success` = `all`/`success`/`failure`; queries are not audited themselves |
| `set_yolo` | `{mode: "off"\|"detect"\|"safe-auto"}` | `{"yoloMode": "..."}` | sets the Win32 modal-dialog auto-handling policy; `safe-auto` only clicks whitelisted buttons (Scene dialogs: Don't Save/Save, Import dialogs: Apply, generic OK/Yes) |
| `bridge_capabilities` | none | `{protocolVersion, capabilities[]}` | protocol version and capability list |

### 4.2 Managed forwarding (requires `managedState == ready`)

When managed is `initializing` / `reloading` / `quitting`, these requests fail immediately (`managed_not_ready` / `managed_reloading` / `managed_quitting`). Requests enter the broker FIFO queue and the C# worker executes them one by one on the `EditorApplication.update` main thread.

| type | payload | result | description |
|------|---------|--------|-------------|
| `execute_code` | `{code}` | eval result (see 6.2) | executes C# on the main thread (Mono.CSharp evaluator) |
| `execute_file` | `{filePath}` | eval result | reads code from a project file and executes; relative paths resolve against the project root, `.repl`/`.cs` supported |
| `validate_execute_code` | `{code}` | eval result | compile-validates before executing (default pi-extension path) |
| `validate_execute_file` | `{filePath}` | eval result | compile-validates the file before executing |
| `validate_code` | `{code}` | `{output, typeName}` (validation-passed text) | compile-only validation, no execution; failure frames carry no `error_type` |
| `validate_file` | `{filePath}` | `{output, typeName}` (validation-passed text) | compile-only validation of a file; failure frames carry no `error_type` |
| `recompile` | none | `{"output": "compilation_succeeded"}` or compile_error | triggers script compilation and waits for completion (blocks at most 120 s) |
| `context_snapshot` | `{maxDepth, maxNodes, logLimit, logLevel, includeComponents}` | context snapshot (Editor state + scene hierarchy + selection + recent logs) | bounded; default maxDepth=3 / maxNodes=500 / logLimit=50 / logLevel=error |
| `list_commands` | none | `{typeName, pipelineAvailable, commands[], count}` | discovers registered `[CliCommand]`s of com.unity.pipeline; `pipelineAvailable=false` when pipeline is not installed |
| `command` | `{name, parametersJson}` | `{output, ...}` | executes a pipeline command; `parametersJson` is the JSON string of the parameters object |

`filePath` resolution: non-absolute paths are joined as `<UnityProject>/<filePath>` and then passed through `GetFullPath`; a missing file responds `file_not_found: <absolute path>` (`error_type=usage`).

## 5. Errors

### 5.1 Broker-level errors (`ok:false`, `error` is the error code)

| error | trigger |
|-------|---------|
| `invalid_json` | the request line is not valid JSON |
| `missing_id_or_type` | `id` or `type` is missing |
| `unauthorized` | token mismatch |
| `request_too_large` | request line exceeds 1 MiB |
| `managed_not_ready` / `managed_reloading` / `managed_quitting` | a forwarding-type request arrives while managed is not ready |
| `request_timeout_before_dispatch` | queue timeout (never delivered to the main thread) |
| `request_timeout_in_flight` | main-thread execution timeout (per `timeoutMs` while heartbeat is healthy) |
| `managed_heartbeat_timeout` | main thread stalled (heartbeat not updated for >5 s and the request in flight >5 s) |
| `client_disconnected` | in-flight request failed because the client disconnected |

`request_timeout_*` and `managed_heartbeat_timeout` are decided by heartbeat: managed reports a heartbeat every <5 s; only on heartbeat timeout is `managed_heartbeat_timeout` used, otherwise `request_timeout_in_flight` is decided by the client `timeoutMs`.

### 5.2 Managed-level errors (with `error_type`)

| error_type | example error |
|------------|---------------|
| `usage` | `empty_code`, `empty_file_path`, `file_not_found: <path>`, `unsupported_request_type`, `request_parse_failed: ...` |
| `compile_error` | validation failure message (may include code-scan hints) |
| `runtime_error` | execution exception message |
| `busy` | `coroutine queue full` |
| `cancelled` | async eval cancelled (e.g. cleanup before domain reload) |

## 6. Result payloads

### 6.1 `status` / state plane status payload

```json
{
  "project": "/path/to/UnityProject",
  "processId": 12345,
  "pipe": "\\\\.\\pipe\\...",
  "statePlaneName": "Local\\PiUnityHarnessState_<hash>",
  "observedAtMs": 1750000000000,
  "connected": true,
  "managedState": "ready",
  "managedGeneration": 3,
  "lastHeartbeatMs": 1750000000000,
  "heartbeatAgeMs": 12,
  "heartbeatTimedOut": false,
  "editorStatus": "editing;focus=background;window=minimized",
  "focusState": "background",
  "windowState": "minimized",
  "pending": 0,
  "inFlight": 0,
  "capabilities": ["native-broker", "state-plane-v1", "..."],
  "modalObservation": { "present": false, "detectedAtMs": 0, "windows": [] },
  "yoloMode": "detect"
}
```

| Field | Description |
|-------|-------------|
| `managedState` | `initializing` / `ready` / `reloading` / `quitting` |
| `connected` | whether a client is currently connected (used as the "occupied" flag in the state plane) |
| `editorStatus` | semicolon-separated status string; first segment is the state (`editing` / `playing` / `reloading` / `quitting`), followed by `key=value` pairs: `focus=foreground|background`, `window=normal|minimized`, `modal=1` (modal dialog present), `mainThreadStale=1` (main thread stalled) |
| `capabilities` | capability list: `native-broker`, `direct-status`, `reload-stable-pipe`, `state-plane-v1`, `background-runner`, `focus-state`, `heartbeat-timeout`, `request-timeout`, `client-heartbeat-timeout`, `context-snapshot-v1`, `action-timeline-v1`, `modal-probe-v1` |
| `modalObservation.windows[]` | `{hwnd, title, class, buttons[]}`; `class` is the Win32 window class (dialogs are `#32770`) |

### 6.2 eval result

```json
{
  "output": "42",
  "typeName": "string",
  "timing": { "evalMs": 3.2, "totalMs": 5.1 }
}
```

- `typeName`: `string` / `void` / `nested_coroutine` (executed across frames when an `IEnumerator` is returned) / other CLR type names
- On failure `ok:false` with `error_type` per 5.2; `error_type=compile_error` may carry an additional `hint` (fix suggestion) and `pattern_violation` (error classification)
- Successful `validate_code` / `validate_file` return the validation-passed text; failure frames carry only the `error` string without `error_type`

### 6.3 `context_snapshot` result

Contains: project path, Unity version and platform, PlayMode/pause/compilation/update state, active scene metadata, main camera, current selection, bounded scene hierarchy (`{name, path, active, components?}`), recent logs (`{level, message}`). Fields follow the C# side implementation; clients MUST NOT assume a fixed field set.

## 7. Action Timeline (operation audit)

- Storage: `<UnityProject>/Temp/PiUnityHarness/ActionTimeline/YYYY-MM-DD.jsonl`, rotated at 5 MB per file
- Each JSONL line is one event; `started` and `completed` are paired via `actionId` + `requestId`
- `started`: `{schemaVersion, event, actionId, requestId, requestType, action, timestampMs, timestampUtc, input}`
- `completed`: `{schemaVersion, event, actionId, requestId, requestType, action, timestampMs, timestampUtc, durationMs, success}` + `result` on success (truncated and redacted) or `errorType`/`error` on failure
- Redaction: keys such as `token` / `accesstoken` / `refreshtoken` / `apikey` / `password` / `secret` / `authorization` / `credential(s)` are recursively replaced with `[REDACTED]`
- eval-type requests record only `codeLength` (raw code is never written); `context_snapshot` arguments are recorded but also redacted
- `timeline` queries return `{schemaVersion, capturedAtMs, capturedAtUtc, directory, count, actions[]}` with `actions[]` ordered by `completedAtMs` descending (falling back to `startedAtMs`)

## 8. State Plane (shared-memory degraded state)

When the pipe is unreachable (domain-reload window, unresponsive Editor), clients can read shared memory for the latest status snapshot.

- Name: `statePlaneName` from bridge.json (`Local\` prefix, process-local)
- Layout (little-endian):

| Offset | Size | Content |
|--------|------|---------|
| 0 | u32 | magic `0x48554950` ("PIUH") |
| 4 | u16 | version = 1 |
| 6 | u16 | slot count = 2 |
| 8 | u32 | slot size = 65536 |
| 12 | u32 | writer process PID |
| 16 | u64 | writer sequence (incremented per publish) |
| 24 | u64 | creation time ms |
| 64 | — | slot[0] (64 KiB) |
| 64+65536 | — | slot[1] (64 KiB) |

- Slot layout: `seq u64 @0`, `payloadLen u32 @16`, payload JSON starts at @24
- Write strategy: ping-pong double buffering, writing to slot `(writerSeq-1) % 2`; readers MUST verify `slot.seq == writerSeq` and consistency before/after reading to avoid torn reads
- The payload is isomorphic to the 6.1 status payload; `connected:true` means the pipe is occupied by a client (the basis for multi-session mutual exclusion)

## 9. Reference client behavior (pi extension)

The pi extension `.pi/extensions/pi-unity-harness/index.ts` is the de-facto reference implementation of this protocol; conventions worth following:

1. Discovery: scan running `Unity.exe` processes (excluding AssetImportWorker / `-batchMode`), read the matching project's `bridge.json`; a `statePlaneName` with `connected=true` counts as occupied
2. Connect: TCP-connect to the pipe (for Node, Windows named pipes are `\\.\pipe\...` paths), line-buffered parsing; start a 5 s heartbeat `ping` after `connect`
3. Timeout: on client-side timer expiry per `timeoutMs`, destroy the socket first, then probe for modal dialogs (one `status` request via `probeModalStatus`); if a dialog is present, report an `EDITOR_MODAL` error asking for manual handling
4. eval file conventions: multi-line C# is written to `<project>/Temp/PiUnityHarness/AgentScratch/*.repl` with the first line `// #repl-mode: top-level|class|auto`; inline code above 512 KiB automatically switches to the file channel
5. After compilation (recompile triggers a domain reload) the connection briefly drops and rebuilds; clients should wait for `status` to return to `ready` before continuing

## 10. Versioning and compatibility

- `NATIVE_PROTOCOL_VERSION = 1`; the managed side passes the protocol version to `pi_unity_init`; on mismatch the broker only warns and does not reject (current forward-compatibility policy)
- Adding request types or response fields is a compatible change; changing frame format, field semantics, or error codes is a breaking change and MUST bump the protocol version
- If this document and the code disagree, this document wins, but the code or the document should be fixed promptly; long-term divergence is not allowed
