#if PI_UNITY_PIPELINE
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.Input;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiInputPipelineCommands
    {
        [CliCommand("input_raycast", "3D physics raycast from top-left GameView coordinates")]
        public static string Raycast(
            [CliArg("x", "X coordinate (top-left GameView)")] float x,
            [CliArg("y", "Y coordinate (top-left GameView)")] float y,
            [CliArg("max_distance", "Max ray distance")] float maxDistance = 1000f,
            [CliArg("layer_mask", "Physics layer mask")] int layerMask = -1)
        {
            int mask = layerMask == -1 ? Physics.DefaultRaycastLayers : layerMask;
            return HarnessInput.RaycastJson(x, y, maxDistance, mask);
        }

        [CliCommand("input_probe", "Probe UI/physics hit at top-left GameView coordinates before clicking")]
        public static string Probe(
            [CliArg("x", "X coordinate (top-left GameView)")] float x,
            [CliArg("y", "Y coordinate (top-left GameView)")] float y,
            [CliArg("max_distance", "Max physics ray distance")] float maxDistance = 1000f,
            [CliArg("layer_mask", "Physics layer mask")] int layerMask = -1,
            [CliArg("include_physics", "Whether to run 3D physics raycast")] bool includePhysics = true)
        {
            int mask = layerMask == -1 ? Physics.DefaultRaycastLayers : layerMask;
            return HarnessInput.ProbeJson(x, y, maxDistance, mask, includePhysics);
        }

        [CliCommand("input_ready_state", "Get input readiness state as JSON")]
        public static string ReadyState()
        {
            return HarnessInput.GetReadyStateJson();
        }

        [CliCommand("input_wait_ready", "Wait for input readiness as JSON")]
        public static Task<string> WaitReady(
            [CliArg("min_frames", "Minimum stable ready frames")] int minFrames = 1,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.WaitForReadyJson(minFrames, timeoutMs), "input_wait_ready", timeoutMs);
        }

        [CliCommand("input_clear_all", "Clear all pressed input state")]
        public static string ClearAll()
        {
            return HarnessInput.ClearAllInputJson();
        }

        [CliCommand("input_press_key", "Press a keyboard key")]
        public static string PressKey([CliArg("key", "Key name")] string key)
        {
            return HarnessInput.PressKeyJson(key);
        }

        [CliCommand("input_release_key", "Release a keyboard key")]
        public static string ReleaseKey([CliArg("key", "Key name")] string key)
        {
            return HarnessInput.ReleaseKeyJson(key);
        }

        [CliCommand("input_key_chord", "Press and release a key chord from JSON key array")]
        public static Task<string> KeyChord(
            [CliArg("keys_json", "JSON array of key names")] string keysJson,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.KeyChordJson(keysJson), "input_key_chord", timeoutMs);
        }

        [CliCommand("input_type_text", "Type text through the active keyboard")]
        public static Task<string> TypeText(
            [CliArg("text", "Text to type")] string text,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.TypeTextJson(text), "input_type_text", timeoutMs);
        }

        [CliCommand("input_mouse_position", "Set mouse position in top-left GameView coordinates")]
        public static string MousePosition(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y)
        {
            return HarnessInput.SetMousePositionJson(x, y);
        }

        [CliCommand("input_mouse_press", "Press a mouse button")]
        public static string MousePress([CliArg("button", "Mouse button")] string button = "left")
        {
            return HarnessInput.PressMouseButtonJson(button);
        }

        [CliCommand("input_mouse_release", "Release a mouse button")]
        public static string MouseRelease([CliArg("button", "Mouse button")] string button = "left")
        {
            return HarnessInput.ReleaseMouseButtonJson(button);
        }

        [CliCommand("input_click", "Click a point in top-left GameView coordinates")]
        public static Task<string> Click(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("button", "Mouse button")] string button = "left",
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.ClickJson(x, y, button), "input_click", timeoutMs);
        }

        [CliCommand("input_double_click", "Double click a point in top-left GameView coordinates")]
        public static Task<string> DoubleClick(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("button", "Mouse button")] string button = "left",
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.DoubleClickJson(x, y, button), "input_double_click", timeoutMs);
        }

        [CliCommand("input_drag", "Drag from one point to another in top-left GameView coordinates")]
        public static Task<string> Drag(
            [CliArg("from_x", "Start X coordinate")] float fromX,
            [CliArg("from_y", "Start Y coordinate")] float fromY,
            [CliArg("to_x", "End X coordinate")] float toX,
            [CliArg("to_y", "End Y coordinate")] float toY,
            [CliArg("button", "Mouse button")] string button = "left",
            [CliArg("steps", "Move steps")] int steps = 8,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.DragJson(fromX, fromY, toX, toY, button, steps), "input_drag", timeoutMs);
        }

        [CliCommand("input_drag_start", "Start a split drag")]
        public static Task<string> DragStart(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("button", "Mouse button")] string button = "left",
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.DragStartJson(x, y, button), "input_drag_start", timeoutMs);
        }

        [CliCommand("input_drag_move", "Move an active split drag")]
        public static Task<string> DragMove(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("steps", "Move steps")] int steps = 0,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.DragMoveJson(x, y, steps), "input_drag_move", timeoutMs);
        }

        [CliCommand("input_drag_end", "End an active split drag")]
        public static Task<string> DragEnd(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("steps", "Move steps")] int steps = 0,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.DragEndJson(x, y, steps), "input_drag_end", timeoutMs);
        }

        [CliCommand("input_drag_cancel", "Cancel an active split drag")]
        public static string DragCancel()
        {
            return HarnessInput.CancelDragJson();
        }

        [CliCommand("input_scroll", "Scroll at a point in top-left GameView coordinates")]
        public static Task<string> Scroll(
            [CliArg("x", "X coordinate")] float x,
            [CliArg("y", "Y coordinate")] float y,
            [CliArg("delta_x", "Horizontal scroll delta")] float deltaX,
            [CliArg("delta_y", "Vertical scroll delta")] float deltaY,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.ScrollJson(x, y, deltaX, deltaY), "input_scroll", timeoutMs);
        }

        [CliCommand("input_sequence", "Run an input action sequence from JSON")]
        public static Task<string> Sequence(
            [CliArg("sequence_json", "Input action sequence JSON")] string sequenceJson,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 3000)
        {
            return PiAbilityCoroutine.ToTask(HarnessInput.RunSequenceJson(sequenceJson), "input_sequence", timeoutMs);
        }
    }
}
#endif
