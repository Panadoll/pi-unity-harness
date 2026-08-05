# pi-unity-harness

[中文](README.md) | English

pi-unity-harness is a bridge between an AI coding agent (pi) and the Unity Editor. It lets the agent execute C# code inside the Unity Editor, trigger compilation and tests, read scene/log snapshots via `unity_*` tools, and reuse the command system of the official Unity `com.unity.pipeline` package.

- `native/` — a Rust `cdylib` that hosts a named pipe server inside the Unity process and survives domain reloads
- `unity/com.pi.unity-harness/` — a Unity Editor package; the C# side handles main-thread execution and domain-reload lifecycle
- `.pi/extensions/pi-unity-harness/` — a pi TypeScript extension exposing connection, context snapshot, action audit, pipeline, and eval tools

## Features

- `unity_ping` / `unity_status`: check broker liveness and Editor runtime state
- `unity_eval` / `unity_eval_file`: execute short C# snippets or multi-line `.repl`/`.cs` files on the Unity Editor main thread
- `unity_recompile`: trigger script compilation and return the result
- `unity_snapshot`: fetch a bounded snapshot of Editor state, active scene hierarchy, current selection, and recent logs
- `unity_timeline`: query the append-only action audit (request summaries, results, durations, success status)
- `unity_pipeline`: discover/execute pipeline `[CliCommand]`s; frequently used commands are dynamically registered as shortcuts (e.g. `unity_run_tests`)
  - Unity 6+: official `com.unity.pipeline`; pre-Unity 6 (2021.3 / 2022): built-in `com.pi.pipeline.compat` compatibility fork

> `com.pi.pipeline.compat` is a compatibility fork of the official Unity `com.unity.pipeline`. Its original license (Unity Package Distribution License) is preserved at `unity/com.pi.pipeline.compat/LICENSE.md`; it is only meant for local embedded installs on pre-Unity 6 projects.

## Requirements

- Windows (only the named pipe transport is implemented so far)
- Unity Editor 2021.3+

## Install

### Install in a Unity project

Add `unity/com.pi.unity-harness` as a local UPM package in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.pi.unity-harness": "file:../pi-unity-harness/unity/com.pi.unity-harness"
  }
}
```

On Editor startup the package automatically initializes the native broker, writes `Library/PiUnityHarness/bridge.json`, and reconnects after domain reloads.

### Install the pi extension

Register it as a global extension in `~/.pi/agent/settings.json` (not `~/.pi/settings.json`):

```json
{
  "extensions": [
    "<path-to-pi-unity-harness>/.pi/extensions/pi-unity-harness"
  ],
  "pi-unity-harness": {
    "enabled": false
  }
}
```

`enabled` defaults to `false` (no `unity_*` tool registered); enable it manually when needed:

```text
/unity-harness on              # current session only
/unity-harness on --persist    # current session + write settings (on by default next time)
/unity-harness off             # disable
/unity-harness status          # show runtime / settings / currently active unity tools
```

Per-project overrides go into `<project>/.pi/settings.json` (same key; project wins over global). Changes to `enabled` take effect in a new pi session (or via `/unity-harness on|off`).

## Usage

```text
unity_ping
unity_status
unity_snapshot { maxDepth: 3, maxNodes: 500, logLimit: 50, logLevel: "error" }
unity_timeline { limit: 20, success: "failure" }
unity_eval { code: "UnityEngine.Debug.Log(123); 123" }
unity_eval_file { filePath: "Temp/PiUnityHarness/AgentScratch/probe.repl" }
unity_recompile
```

## License

The code in this repository (`native/`, `unity/com.pi.unity-harness/`, `.pi/`, `docs/`) is released under the MIT License — see [LICENSE](LICENSE).

`unity/com.pi.pipeline.compat/` is a compatibility fork of the official Unity `com.unity.pipeline` and is released under the Unity Package Distribution License — see `unity/com.pi.pipeline.compat/LICENSE.md`.
