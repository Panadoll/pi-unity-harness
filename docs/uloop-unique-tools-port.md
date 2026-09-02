# uloop V3 unique tools ported into pi-unity-harness

Source: `F:/Projects-Test/unity-ai-tool/unity-cli-loop` (package 3.1.0).
Target: `unity/com.pi.unity-harness/Vendor/Uloop` plus `[CliCommand]` wrappers.

MCP is not used. Commands are discovered through pipeline metadata (`pi-unity list-commands` / `pi-unity pipeline <name>`).

## Commands

| Pipeline command | uloop origin |
| --- | --- |
| `hot_reload` / `hot_reload_status` / `hot_reload_revert_all` | source-level Harmony/Cecil instant patch, no recompile |
| `pause_point_enable` / `pause_point_clear` / `pause_point_status` / `pause_point_await` | Harmony injection + captured variables |
| `input_record_start` / `input_record_stop` / `input_record_status` | Editor recordings (Input System) |
| `input_replay` / `input_replay_stop` / `input_replay_status` | replay recorded JSON |
| `vision_annotate_raycast` | clustered physics collider annotations |

Existing `vision_annotate` / `input_raycast` remain the harness-native grid path.

## Tests reused from uloop

- `Tests/Editor/UloopPort/HotReloadFileSystemPathTests.cs`
- `Tests/Editor/UloopPort/RaycastGridAnnotatorTests.cs`
- `Tests/Editor/PipelineDiscovery/UloopUniqueToolsDiscoveryTests.cs` (new TDD discovery tests)

End-to-end Harmony patch / TransformWorker tests stay in the uloop repo (they need that project's fixtures and PlayMode scenes).

## Dependencies added

- `com.unity.nuget.mono-cecil`
- Vendored Harmony (`UnityCliLoop.0Harmony.dll`) and Roslyn worker helper DLLs

Watch expressions (`enable-watch`) were not ported; they require the full execute-dynamic-code compilation service.
