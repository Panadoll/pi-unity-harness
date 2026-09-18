# pi-unity-harness

[中文](README.md) | English

`pi-unity-harness` is a command-line bridge (CLI-First / No-MCP) connecting AI coding agents with the Unity Editor. Through the native `pi-unity` CLI binary and Agent Skills, any AI coding agent (Claude Code, Codex, Antigravity, Pi, Aider, etc.) or developer can directly drive Unity Editor over shell commands to execute C# code, trigger compilation, run test suites, capture scene snapshots, and perform perceptual playtesting.

- `native/` — Rust implementation: Native Broker (`cdylib` plugin, survives domain reloads) + standalone `pi-unity` native CLI binary
- `unity/com.pi.unity-harness/` — Unity Editor package; C# side handles main-thread dispatching and Pipeline command execution
- `skills/` — One `pi-unity` Agent Skill (details in `references/`). `pi-unity skills install` copies the whole skill directory to `.agents/skills/` or `.claude/skills/`. A later install removes the old nine split skills when a directory contains only a matching `SKILL.md`
- `.pi/extensions/pi-unity-harness/` — Thin wrapper providing typed tools for pi-coding-agent (shells out to `pi-unity` CLI)

## pi Extension Mux

The pi-coding-agent extension spawns the hidden `pi-unity mux` subcommand at `session_start`; tool calls go through stdin/stdout JSONL instead of spawning the CLI each time. Each reply carries `result` (condensed JSON) and `text` (Rust TOON of the same result). JSONL is IPC only; the model sees `text`.

The mux holds one Unity named pipe, pinging every 5 s while connected (the broker drops idle connections after 15 s). The broker stays **single-client**: one Editor serves one client at a time and only the main session may drive it; subprocess / worktree subagents must use their own dedicated Editor. Other short-lived CLIs may see `Pipe busy` while the mux is alive — this is not a multi-agent channel, and slash UI is not restored.

Requests are FIFO-queued on the extension side: at most one business frame is written to stdin at a time; queuing does not consume timeout. Cancelling a queued request only removes that one. On **unexpected mux exit**, queued requests that were never written (not dispatched) are resent once with the new mux process; on **timeout / abort / protocol damage** the whole queue fails without resend. **Dispatched (in-flight) requests are never replayed.** The `execFile` fallback applies only when the mux never started at all.

Discovery precedence: explicit selection (`/unity-discover` / `--project-path`) wins, then session-cwd local discovery, then environment fallback (`UNITY_PROJECT_PATH`).

---

## Key Features

- **Pure CLI Architecture (No-MCP)**: No Node.js runtime dependency, low startup overhead, decoupled lifecycle, and process crash immunity.
- **Dual-Mode Workflow**:
  - **Speed Mode (Default)**: For UI, start with `uitree_snapshot interactive_only=true`, then locate with `uitree_find` / `input_probe`, then act (drag in one `input_drag` call); for non-UI or custom state use `snapshot` + `eval`. Text-only white-box inspection, no large images, saves tokens.
  - **GUI Mode (On-Demand)**: `observe` + `capture` multi-frame capture and dHash change detection, only for visual mismatches or custom-rendered UI. Large images are saved to disk (`Temp/PiUnityHarness/Captures/`), never dumping Base64 to stdout, no per-step image reads; `embed:false`-style markers are advisory only — hosts do not enforce inline prevention.
- **Automatic Domain Reload Reconnection**: `pi-unity compile` manages connection drop and polls until `managedState == "ready"`.
- **AXI output**: stdout defaults to TOON; `--json` opts into JSON. Bare `pi-unity` prints a live dashboard. Exit codes: `0` success (including idempotent no-ops), `1` error (including not connected / timeout), `2` usage.

---

## CLI Command Reference

| CLI Command | Options | Description |
| :--- | :--- | :--- |
| `pi-unity` | (none) | Live dashboard (bin / editor state / next steps) |
| `pi-unity ping` | `--timeout <ms>` | Probe Unity Broker |
| `pi-unity status` | `--json` `--full` | Editor status, generation, focus, modal |
| `pi-unity eval <code>` | `-f, --file <path>` | Run C# on the main thread |
| `pi-unity compile` | `--timeout <ms>` | Compile and wait until ready |
| `pi-unity snapshot` | `--depth` `--max-nodes` `--fields` `--full` | Hierarchy (default path,name,active), selection, logs |
| `pi-unity list-commands` | `--full` | Pipeline commands (default name,summary) |
| `pi-unity pipeline <name>` | `-p <key=val>` `--params-json` | Run a `[CliCommand]` |
| `pi-unity run-tests` | `--mode <edit\|play>` `--filter` | EditMode / PlayMode tests |
| `pi-unity observe` | `--frames` `--interval` `--overlay` | Multi-frame capture + dHash; paths only |
| `pi-unity capture` | `--mode` `--out` | Single screenshot |
| `pi-unity timeline` | `--limit` `--success` | Audit history (default id,name,ok) |
| `pi-unity setup` | `--project` | Install SessionStart hooks (Claude / Codex / OpenCode) |
| `pi-unity skills install` | `--agents` `--claude` `--target` | Install the skill; `skills check` fails on drift |

---

## Quick Start

### 1. Install Harness in Unity Project

Add `unity/com.pi.unity-harness` as a local UPM package in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.pi.unity-harness": "file:../pi-unity-harness/unity/com.pi.unity-harness"
  }
}
```

### 2. Build and Run CLI

Run the build script in the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-native.ps1
```

The compiled `pi-unity.exe` is located in `bin/` and `dist/`:

```bash
# Verify connection
pi-unity ping

# Check Editor status
pi-unity status

# Evaluate C# expression
pi-unity eval "UnityEngine.Application.unityVersion"

# Install Skills to .agents/skills/
pi-unity skills install --agents
```

---

## Verify Loop Workflow

```text
1. Observe   → pi-unity snapshot (baseline observation)
2. Act       → apply changes to code / assets / scene
3. Compile   → pi-unity compile (await domain reload, verify 0 errors)
4. Verify    → pi-unity run-tests --mode edit or pi-unity eval probe
5. Re-observe→ pi-unity snapshot (compare post-change state with expected outcome)
```

---

## Observability & Logging

`pi-unity-harness` features built-in lightweight observability (L0 collection and L1 storage), recording all agent interactions and CLI calls for diagnostics, metrics, and continuous tool improvement.

### 1. Storage Layout (`~/.pi-unity/`, overridable via `PI_UNITY_LOG_DIR`)
- `logs/events-YYYY-MM.jsonl`: Unified event stream (one line per CLI invocation, recording execution time, phases, exit code, and `errorType`, rotated monthly).
- `logs/traces/YYYY-MM-DD/`: Detailed execution traces (written on failures or when `--trace` / `PI_UNITY_TRACE=1` is active, retained for 7 days).
- `sessions/current.json`: Sticky session registry (12-hour TTL; one file per user, so parallel agents overwrite each other).

### 2. Session and Skill Tracking Commands
```bash
# Start sticky session
pi-unity session start --task "Refactor battle flow"

# Optional extra skill mark (each CLI call already logs an event)
pi-unity mark --skill pi-unity --event used

# End active session
pi-unity session end
```

### 3. Privacy Redlines & Best-Effort Delivery
- **Privacy Redlines**: Never writes C# code, parameter values, auth tokens, or raw error messages (only normalized `errorType`). Project paths are recorded only as a 6-byte SHA256 hex hash (`projectHash`).
- **Best-Effort**: Call-log and trace write failures never change CLI behavior or exit codes. `session start` is the exception: it fails if the registry file cannot be written.
- **Skill marks**: `pi-unity mark` is a voluntary signal, not automatic usage counting.

---

## License & Third-Party Notices

Core repository code is licensed under the **MIT License** — see [LICENSE](LICENSE).

### Third-Party Acknowledgements

This project integrates the following open-source projects and libraries:

- **[UnityCliLoop (Uloop)](https://github.com/hatayama)** (MIT License) - Provides method-level hot reloading (Hot Reload V3) and runtime pause points (Pause Point), located under `unity/com.pi.unity-harness/Vendor/Uloop/`.
- **[Lib.Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike (MIT License) - Provides runtime IL method patching and JIT hooks (vendored as `UnityCliLoop.0Harmony.dll`, see `unity/com.pi.unity-harness/Vendor/Uloop/Editor/PausePoint/Plugins/LICENSE.md`).
- **[.NET Roslyn Libraries](https://github.com/dotnet/roslyn)** by .NET Foundation (MIT License) - Provides in-memory C# dynamic code analysis and compilation metadata, see `unity/com.pi.unity-harness/Vendor/Uloop/Editor/Compiler/Plugins/CodeAnalysis/LICENSE.md`.
- **Unity.Pipeline** by Unity Technologies - `unity/com.pi.pipeline.compat/` is released under the Unity Package Distribution License — see `unity/com.pi.pipeline.compat/LICENSE.md`.
