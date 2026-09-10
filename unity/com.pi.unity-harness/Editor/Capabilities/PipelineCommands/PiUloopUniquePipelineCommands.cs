#if PI_UNITY_PIPELINE
using System;
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.FirstPartyTools;
using io.github.hatayama.UnityCliLoop.Runtime;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pi.UnityHarness.Editor.Capabilities.VendorGlue;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUloopUniquePipelineCommands
    {
        [CliCommand("hot_reload", "Apply method-body source patches without recompiling (uloop V3)")]
        public static Task<string> HotReload(
            [CliArg("files_json", "JSON array of project-relative .cs paths; empty selects files changed since last compile")] string filesJson = "[]",
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 120000)
        {
            var schema = new HotReloadSchema
            {
                Files = ParseStringArray(filesJson)
            };
            return Run(ct => new HotReloadTool().ExecuteAsync(JObject.FromObject(schema), ct), timeoutMs);
        }

        [CliCommand("hot_reload_status", "List currently applied hot-reload patches")]
        public static Task<string> HotReloadStatus(
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 30000)
        {
            var schema = new HotReloadSchema { Status = true };
            return Run(ct => new HotReloadTool().ExecuteAsync(JObject.FromObject(schema), ct), timeoutMs);
        }

        [CliCommand("hot_reload_revert_all", "Revert every active hot-reload patch")]
        public static Task<string> HotReloadRevertAll(
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 30000)
        {
            var schema = new HotReloadSchema { RevertAll = true };
            return Run(ct => new HotReloadTool().ExecuteAsync(JObject.FromObject(schema), ct), timeoutMs);
        }

        [CliCommand("pause_point_enable", "Harmony-inject a source file:line pause point and capture variables")]
        public static Task<string> PausePointEnable(
            [CliArg("file", "Project-relative source file")] string file,
            [CliArg("line", "1-based line number")] int line,
            [CliArg("mode", "single-shot, continuous, or trace")] string mode = "single-shot",
            [CliArg("timeout_seconds", "Arm timeout")] int timeoutSeconds = 30,
            [CliArg("method", "Optional method filter")] string method = "",
            [CliArg("persist", "Re-arm this pause point automatically after a domain reload")] bool persist = false,
            [CliArg("snapshot_timing", "pre-line or post-line; post-line captures values right after the line ran")] string snapshotTiming = "pre-line",
            [CliArg("timeout_ms", "Command timeout")] int timeoutMs = 30000)
        {
            var schema = new EnablePausePointSchema
            {
                File = file ?? string.Empty,
                Line = line,
                Mode = mode ?? "single-shot",
                TimeoutSeconds = timeoutSeconds,
                Method = method ?? string.Empty,
                Persist = persist,
                SnapshotTiming = string.IsNullOrEmpty(snapshotTiming) ? "pre-line" : snapshotTiming
            };
            return Run(ct => new EnablePausePointTool().ExecuteAsync(JObject.FromObject(schema), ct), timeoutMs);
        }

        [CliCommand("pause_point_clear", "Clear one pause point by id, or all when all=true")]
        public static Task<string> PausePointClear(
            [CliArg("id", "Pause point id")] string id = "",
            [CliArg("all", "Clear every pause point")] bool all = false,
            [CliArg("timeout_ms", "Command timeout")] int timeoutMs = 15000)
        {
            var schema = new ClearPausePointSchema
            {
                Id = id ?? string.Empty,
                All = all
            };
            return Run(ct => new ClearPausePointTool().ExecuteAsync(JObject.FromObject(schema), ct), timeoutMs);
        }

        [CliCommand("pause_point_status", "Read pause-point registry snapshots including captured variables")]
        public static string PausePointStatus([CliArg("id", "Optional pause point id")] string id = "")
        {
            if (!string.IsNullOrEmpty(id))
            {
                UloopPausePointSnapshot snapshot = UloopPausePointRegistry.GetStatus(id);
                return JsonConvert.SerializeObject(snapshot);
            }

            return JsonConvert.SerializeObject(UloopPausePointRegistry.GetAllStatuses());
        }

        [CliCommand("pause_point_await", "Wait until a pause point hits or expires")]
        public static async Task<string> PausePointAwait(
            [CliArg("id", "Pause point id")] string id,
            [CliArg("timeout_ms", "Wait timeout")] int timeoutMs = 30000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                UloopPausePointSnapshot snapshot = UloopPausePointRegistry.GetStatus(id);
                if (snapshot != null && (snapshot.IsHit || snapshot.Expired || !snapshot.IsEnabled))
                    return JsonConvert.SerializeObject(snapshot);
                await Task.Delay(50);
            }

            return JsonConvert.SerializeObject(new
            {
                success = false,
                message = "Timed out waiting for pause point '" + id + "'."
            });
        }

        [CliCommand("input_record_start", "Start Input System recording (PlayMode)")]
        public static string InputRecordStart(
            [CliArg("delay_seconds", "Countdown before recording")] int delaySeconds = 0)
        {
            var service = new RecordingsApplicationService();
            RecordingApplicationResult result = service.ToggleRecording(delaySeconds);
            return JsonConvert.SerializeObject(new
            {
                success = !result.ShouldShowDialog,
                message = result.DialogMessage,
                state = service.GetCurrentState()
            });
        }

        [CliCommand("input_record_stop", "Stop Input System recording and write JSON")]
        public static string InputRecordStop()
        {
            var service = new RecordingsApplicationService();
            RecordingApplicationState state = service.GetCurrentState();
            if (!state.IsRecording)
            {
                return JsonConvert.SerializeObject(new { success = false, message = "Not recording." });
            }

            RecordingApplicationResult result = service.ToggleRecording(0);
            return JsonConvert.SerializeObject(new
            {
                success = !result.ShouldShowDialog,
                message = result.DialogMessage,
                state = service.GetCurrentState()
            });
        }

        [CliCommand("input_record_status", "Input recording / replay overlay status")]
        public static string InputRecordStatus()
        {
            var service = new RecordingsApplicationService();
            return JsonConvert.SerializeObject(service.GetCurrentState());
        }

        [CliCommand("input_replay", "Replay a recorded Input System JSON file")]
        public static Task<string> InputReplay(
            [CliArg("input_path", "Recording JSON path; empty uses latest")] string inputPath = "",
            [CliArg("loop", "Loop the recording")] bool loop = false,
            [CliArg("show_overlay", "Show replay overlay")] bool showOverlay = true,
            [CliArg("timeout_ms", "Timeout")] int timeoutMs = 15000)
        {
            var token = new JObject
            {
                ["action"] = "start",
                ["inputPath"] = inputPath ?? string.Empty,
                ["loop"] = loop,
                ["showOverlay"] = showOverlay
            };
            return Run(ct => new ReplayInputTool().ExecuteAsync(token, ct), timeoutMs);
        }

        [CliCommand("input_replay_stop", "Stop an in-progress input replay")]
        public static Task<string> InputReplayStop(
            [CliArg("timeout_ms", "Timeout")] int timeoutMs = 15000)
        {
            var token = new JObject { ["action"] = "stop" };
            return Run(ct => new ReplayInputTool().ExecuteAsync(token, ct), timeoutMs);
        }

        [CliCommand("input_replay_status", "Input replay status")]
        public static Task<string> InputReplayStatus(
            [CliArg("timeout_ms", "Timeout")] int timeoutMs = 15000)
        {
            var token = new JObject { ["action"] = "status" };
            return Run(ct => new ReplayInputTool().ExecuteAsync(token, ct), timeoutMs);
        }

        [CliCommand("vision_annotate_raycast", "uloop clustered physics collider raycast annotations")]
        public static string VisionAnnotateRaycast(
            [CliArg("layer_mask", "Physics layer mask")] int layerMask = -1)
        {
            int mask = layerMask == -1 ? Physics.DefaultRaycastLayers : layerMask;
            return RaycastAnnotationGlue.CollectJson(mask);
        }

        private static Task<string> Run<T>(Func<CancellationToken, Task<T>> action, int timeoutMs)
        {
            return PiUloopToolRunner.Run(action, timeoutMs);
        }

        private static string[] ParseStringArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]")
                return Array.Empty<string>();
            try
            {
                return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
#endif
