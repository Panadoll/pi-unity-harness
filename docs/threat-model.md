# Threat Model

This document describes the security boundary of the local Pi Unity Harness bridge. It is a design record, not a promise that the bridge is a sandbox.

## 1. Trust Boundary

Trusted principals are the local Agent process and the Unity Editor process running the harness. Untrusted inputs include other processes running as the same user, malformed CLI and JSONL input, stale `bridge.json` files, stale pipe clients, and Pipeline command implementations supplied by the project.

The bridge is local-process automation. It is not an isolation boundary: an Agent that can connect can request operations with the authority of the Editor, and a same-user process may be able to read the bridge files.

## 2. Authentication

`PiUnityBridge.Session.cs:GetOrCreateToken` creates a 32-hex-character token with `Guid.NewGuid().ToString("N")`. `WriteBridgeInfo` stores it in `Library/PiUnityHarness/bridge.json`; the native client reads that file before connecting. The token is kept in Unity `SessionState`, so it survives ordinary managed reloads in the Editor session and is recreated when no value exists. It is not a cryptographic identity for the user or project.

A same-user process with filesystem access can read `bridge.json`, including the token. This is an accepted local risk. File permissions and process isolation are provided by the host OS rather than by the protocol. The generation field and project-specific pipe name help reject stale discovery data, but do not replace authentication.

## 3. Transport

On Windows the bridge uses a named pipe whose name is written as `\\.\pipe\<name>` by `PiUnityBridge.Session.cs:GetOrCreatePipeName` and `WriteBridgeInfo`. The Rust client connects through the Windows named-pipe implementation in `native/src/bin/pi_unity/client.rs`.

The current design relies on the operating system's default named-pipe DACL. The protocol does not install a custom ACL or claim a stronger current-user-only guarantee. Therefore local same-user access must be treated as an accepted risk; deployments requiring stronger isolation need an explicit ACL design and audit.

## 4. Execution Surface

- `eval` executes arbitrary C# in the Editor process. Its impact is effectively full project and Editor authority; callers must treat code and files as destructive-capable input. The request and file handling are implemented by the bridge command path and `PiUnityJsonHelper`.
- Pipeline commands run project-provided command implementations. Their impact depends on the command and may include changing scenes, assets, project settings, or builds. Discovery metadata is produced by `PiUnityPipelineCommandExecutor.cs`; the extension currently excludes infrastructure commands through `helpers.ts:PIPELINE_TOOL_EXCLUDE`.
- `compile` requests script compilation and domain reload. It can interrupt in-flight work and cause Editor-side state transitions, but it does not itself grant authority beyond the Editor.
- Input and screenshot operations can affect the Editor or a running game. Their exact impact is bounded by the requested coordinates and the active window, not by the pipe protocol.
- YOLO safe-auto can click a detected modal button. Its mode and cooldown are broker state in `native/src/bin/pi_unity/imp.rs`; it is intentionally conservative but remains an automation action.

The typed extension excludes `eval`, `recompile`, `recompile_status`, `editor_status`, and `run_tests` in `.pi/extensions/pi-unity-harness/helpers.ts:PIPELINE_TOOL_EXCLUDE`. Project-specific forbidden command policy is applied by the Pipeline command executor and must be reviewed together with the project command registry.

## 5. YOLO Safe-Auto Allowlist

The current `resolve_yolo_button` heuristic in `native/src/bin/pi_unity/imp.rs` is ordered as follows:

1. A scene and modified title selects `Don't Save` when present.
2. The same dialog selects `Save` when `Don't Save` is absent.
3. An import title selects `Apply` when present.
4. Any dialog with `OK` selects `OK`.
5. Any generic yes/no dialog selects `Yes`.
6. Any remaining dialog selects its first button.

`Don't Save` can discard unsaved scene changes and is the explicit data-loss risk. `Save`, `Apply`, and `Yes` may also have project-specific side effects; the generic fallback is especially dependent on the dialog's button ordering. Safe-auto must therefore remain opt-in and observable.

## 6. Avoiding the Wrong Project

Discovery order is: explicit `--project-path`, `UNITY_PROJECT_PATH`, current working directory and up to five parents (`native/src/bin/pi_unity/discovery.rs:resolve_project_root_fast`), then Windows process command-line discovery in `resolve_project_root`. `load_bridge_json` validates and parses the selected project's `Library/PiUnityHarness/bridge.json`.

The CLI status and state payloads echo `project`, and the mux startup path should validate the selected project against the requested project path before sending work. Operators should use `--project-path` for automation involving multiple open projects and inspect the returned `project` before destructive commands.

## 7. Headless and CI Environments

`PiUnityBridge.Session.cs:ShouldRunInCurrentProcess` disables the managed bridge in batch mode and for AssetImportWorker processes. In such a process there may be no bridge or pipe, so eval and control requests should fail as unavailable rather than assume an interactive Editor.

Native modal probing is Windows UI enumeration. In a headless or CI session it normally finds no visible modal windows; it cannot make a hidden or remote dialog safe. Eval still requires a responsive managed worker, and compile/domain reload behavior depends on the Unity process being available. CI should prefer fixture tests and explicit failure handling over YOLO or UI assumptions.

## 8. Accepted Risks and Future Work

Accepted risks:

- Same-user processes may read `bridge.json` and connect with its token.
- `eval` is intentionally powerful and is not sandboxed.
- Pipeline commands are project code and their metadata is advisory unless policy rejects them.
- Default named-pipe ACL behavior is not a documented custom current-user-only ACL.
- YOLO heuristics can choose a destructive or project-specific button.
- Stale discovery files can exist until replaced or removed.

Future work (not promised by this document):

- Define and test an explicit named-pipe ACL policy.
- Rotate tokens on every Editor/domain-reload boundary if compatibility permits.
- Add a capability or approval layer for destructive eval and Pipeline operations.
- Replace UI heuristics with an explicit user-confirmation channel.
- Add a signed or authenticated project identity to discovery metadata.
