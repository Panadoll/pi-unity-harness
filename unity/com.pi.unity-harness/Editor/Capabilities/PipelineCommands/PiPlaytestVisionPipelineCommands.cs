#if PI_UNITY_PIPELINE
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    /// <summary>
    /// playtest-loop-borrow-plan Phase 1 感知原语：vision_observe / vision_capture_after。
    /// 只新增命令，不改 vision_capture* 默认行为。
    /// </summary>
    internal static class PiPlaytestVisionPipelineCommands
    {
        [CliCommand("vision_observe",
            "Capture N end-of-frame frames with pixel-level dedup, dHash fingerprint, change detection, optional 0-1000 grid overlay, and a timeline sheet when the scene changed")]
        public static Task<string> Observe(
            [CliArg("mode", "Capture mode: game or auto (PlayMode required)")] string mode = "game",
            [CliArg("frames", "Number of frames to capture (1-8, default 3)")] int frames = 3,
            [CliArg("interval_ms", "Minimum wall-clock gap between frames in ms")] int intervalMs = 160,
            [CliArg("overlay", "Overlay mode: none, grid, annotations, or both")] string overlay = "both",
            [CliArg("path_prefix", "Output path prefix (relative to project root or absolute)")] string pathPrefix = null,
            [CliArg("per_frame_timeout_ms", "Per-frame present timeout in ms (EOF not firing, e.g. minimized Editor)")] int perFrameTimeoutMs = 500,
            [CliArg("total_timeout_ms", "Total timeout in milliseconds (default 5000)")] int totalTimeoutMs = 5000)
        {
            // ToTask 超时需大于内部 totalTimeout（内部超时后还要留给 runner 退出并
            // 返回 timed_out JSON 的余量），避免泵先抛 TimeoutException
            return PiAbilityCoroutine.ToTask(
                PlaytestVision.ObserveJsonAsync(
                    mode, frames, intervalMs, overlay, pathPrefix, totalTimeoutMs, perFrameTimeoutMs),
                "vision_observe",
                totalTimeoutMs + 10000);
        }

        [CliCommand("vision_capture_after",
            "Capture a post-action burst sequence: short (8 frames @120ms) or burst (24 frames @80ms), composed into a timestamped sheet")]
        public static Task<string> CaptureAfter(
            [CliArg("mode", "Burst mode: short or burst")] string mode = "short",
            [CliArg("path_prefix", "Output path prefix (relative to project root or absolute)")] string pathPrefix = null,
            [CliArg("per_frame_timeout_ms", "Per-frame present timeout in ms (EOF not firing, e.g. minimized Editor)")] int perFrameTimeoutMs = 500,
            [CliArg("total_timeout_ms", "Total timeout in milliseconds (default 15000)")] int totalTimeoutMs = 15000)
        {
            return PiAbilityCoroutine.ToTask(
                PlaytestVision.CaptureAfterJsonAsync(mode, pathPrefix, totalTimeoutMs, perFrameTimeoutMs),
                "vision_capture_after",
                totalTimeoutMs + 10000);
        }
    }
}
#endif
