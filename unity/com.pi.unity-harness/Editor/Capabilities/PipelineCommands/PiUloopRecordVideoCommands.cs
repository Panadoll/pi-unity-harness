#if PI_UNITY_PIPELINE
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.FirstPartyTools;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUloopRecordVideoCommands
    {
        [CliCommand("record_video", "Record the Game View (PlayMode) or an Editor window to mp4")]
        public static Task<string> RecordVideo(
            [CliArg("action", "start, stop, or status")] string action = "status",
            [CliArg("frame_rate", "Frames per second")] int frameRate = 30,
            [CliArg("max_duration_seconds", "Auto-stop after N seconds")] int maxDurationSeconds = 60,
            [CliArg("output_path", "Absolute output path; empty uses the project default")] string outputPath = "",
            [CliArg("window_name", "Editor window to record; empty records the Game View")] string windowName = "",
            [CliArg("match_mode", "exact, prefix, or contains")] string matchMode = "exact",
            [CliArg("resolution_scale", "Scale factor applied to the source size")] float resolutionScale = 1.0f,
            [CliArg("quality", "low, medium, or high")] string quality = "medium",
            [CliArg("timeout_ms", "Command timeout")] int timeoutMs = 120000)
        {
            var token = new JObject
            {
                ["action"] = action,
                ["frameRate"] = frameRate,
                ["maxDurationSeconds"] = maxDurationSeconds,
                ["outputPath"] = outputPath ?? string.Empty,
                ["windowName"] = windowName ?? string.Empty,
                ["matchMode"] = matchMode,
                ["resolutionScale"] = resolutionScale,
                ["quality"] = quality
            };
            return PiUloopToolRunner.Run(
                ct => new RecordVideoTool().ExecuteAsync(token, ct),
                timeoutMs);
        }
    }
}
#endif
