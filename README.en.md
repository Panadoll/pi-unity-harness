# pi-unity-harness

[中文](README.md) | English

`pi-unity-harness` is a command-line bridge (CLI-First / No-MCP) connecting AI coding agents with the Unity Editor. Through the native `pi-unity` CLI binary and Agent Skills, any AI coding agent (Claude Code, Codex, Antigravity, Pi, Aider, etc.) or developer can directly drive Unity Editor over shell commands to execute C# code, trigger compilation, run test suites, capture scene snapshots, and perform perceptual playtesting.

- `native/` — Rust implementation: Native Broker (`cdylib` plugin, survives domain reloads) + standalone `pi-unity` native CLI binary
- `unity/com.pi.unity-harness/` — Unity Editor package; C# side handles main-thread dispatching and Pipeline command execution
- `skills/` — Fine-grained Agent Skills source files, installable to `.agents/skills/` or `.claude/skills/` via `pi-unity skills install`
- `.pi/extensions/pi-unity-harness/` — Thin wrapper providing typed tools for pi-coding-agent (shells out to `pi-unity` CLI)

---

## Key Features

- **Pure CLI Architecture (No-MCP)**: No Node.js runtime dependency, millisecond startup time, decoupled lifecycle, and process crash immunity.
- **Dual-Mode Workflow**:
  - **Speed Mode (Default)**: `snapshot` + `eval` + `uitree_*` white-box inspection, takes tens of milliseconds, no large images, saves tokens.
  - **GUI Mode (On-Demand)**: `observe` + `capture` multi-frame capture and dHash change detection. Large images are saved to disk (`Temp/PiUnityHarness/Captures/`), never dumping Base64 to stdout.
- **Automatic Domain Reload Reconnection**: `pi-unity compile` manages connection drop and polls until `managedState == "ready"`.
- **Standard Exit Codes & Formatting**: Human-readable and `--json` structured outputs; Exit Code `0` (Success), `1` (Runtime Error), `2` (Not Connected), `3` (Timeout).

---

## CLI Command Reference

| CLI Command | Options | Description |
| :--- | :--- | :--- |
| `pi-unity ping` | `--timeout <ms>` | Probe Unity Broker responsiveness |
| `pi-unity status` | `--json` | Get Editor status, domain reload generation, focus and modal dialogs |
| `pi-unity eval <code>` | `-f, --file <path>` | Execute C# code or expression in Unity main thread |
| `pi-unity compile` | `--timeout <ms>` | Trigger script compilation and wait for domain reload to ready |
| `pi-unity snapshot` | `--depth <N>` `--max-nodes <N>` `--log-limit <N>` `--log-level <error\|warning\|all>` `--no-components` | Get active scene hierarchy, components, selection, and logs |
| `pi-unity list-commands` | `--json` | List all registered Pipeline `[CliCommand]`s |
| `pi-unity pipeline <name>` | `-p <key=val>` `--params-json <json>` | Execute a registered Unity Pipeline command |
| `pi-unity run-tests` | `--mode <edit\|play>` `--filter <pattern>` | Run Unity UTF test suites (EditMode / PlayMode) |
| `pi-unity observe` | `--frames <N>` `--interval <ms>` `--overlay <grid\|annotations\|both\|none>` | Multi-frame vision observation + dHash change detection |
| `pi-unity capture` | `--mode <game\|scene>` `--out <path>` | Viewport screenshot capture (Speed mode) |
| `pi-unity timeline` | `--limit <N>` `--success <all\|success\|failure>` | Query recent operations timeline and audit history |
| `pi-unity skills install` | `--agents` `--claude` `--target <dir>` | Install Agent Skills into target project |

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

## License

Core repository code is licensed under the MIT License — see [LICENSE](LICENSE).
`unity/com.pi.pipeline.compat/` is released under the Unity Package Distribution License — see `unity/com.pi.pipeline.compat/LICENSE.md`.
