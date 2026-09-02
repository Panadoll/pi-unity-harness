# Real tests on connected Unity (GP1)

You are already connected to Unity project **GP1**. Run **real** Editor tests and pipeline smokes. Do not skip Unity. Do not only read code.

## Goal

Verify the uloop V3 unique tools recently ported into `com.pi.unity-harness` actually work in this live Editor.

## Steps

1. `unity_status` / `unity_ping`. Confirm `managedState: ready` and project is GP1. If not ready, wait and retry.
2. `unity_pipeline({})` (empty / list commands). Confirm these names exist:
   - `hot_reload`, `hot_reload_status`, `hot_reload_revert_all`
   - `pause_point_enable`, `pause_point_clear`, `pause_point_status`, `pause_point_await`
   - `input_record_start`, `input_record_stop`, `input_record_status`
   - `input_replay`, `input_replay_stop`, `input_replay_status`
   - `vision_annotate_raycast`
3. Run EditMode tests (real Test Runner, not eval stubs):
   - `unity_run_tests` mode `editor`, filter `Pi.UnityHarness`
   - If that is too broad, also run with filter containing `UloopUniqueToolsDiscovery`, `HotReloadFileSystemPath`, `RaycastGridAnnotator`
4. Pipeline smokes (safe, no PlayMode required unless noted):
   - `unity_pipeline` `hot_reload_status`
   - `unity_pipeline` `pause_point_status`
   - `unity_pipeline` `input_record_status`
   - `unity_pipeline` `input_replay_status`
   - `unity_pipeline` `vision_annotate_raycast`
5. If EditMode tests fail to compile, capture `unity_recompile` / console errors and quote them.
6. If PlayMode is available and idle, optionally `unity_run_tests` mode `playmode` filter `Pi.UnityHarness`. Do not force-start a long play session if the user is in the middle of gameplay.

## Report

Write a short report covering:
- Unity project path and ready state
- Which new commands were discovered (yes/no per name)
- Test counts: passed / failed / skipped, with failing test names
- Smoke command results (success or exact error)
- Compile errors if any

Do not claim tests passed unless `unity_run_tests` returned passing counts.
