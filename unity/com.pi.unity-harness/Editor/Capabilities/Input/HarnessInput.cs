using System;
using System.Collections;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    /// <summary>
    /// Public Input API facade. JSON methods are intended for uh eval -f recipes.
    /// </summary>
    public static partial class HarnessInput
    {
        private const string BackendTypeName =
            "Pi.UnityHarness.Editor.Capabilities.Input.HarnessInputBackend, Pi.UnityHarness.InputSystem.Editor";

        public static bool IsAvailable => BackendType != null;

        /// <summary>
        /// 3D physics raycast from top-left GameView coordinates (does not require PlayMode).
        /// </summary>
        public static string RaycastJson(
            float x,
            float y,
            float maxDistance = PiGameViewPhysicsRaycast.DefaultMaxDistance,
            int layerMask = Physics.DefaultRaycastLayers)
        {
            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError) ||
                !TryValidateFinite("max_distance", maxDistance, out numberError))
                return Fail(numberError, "usage");

            var result = PiGameViewPhysicsRaycast.RaycastFromInput(x, y, maxDistance, layerMask, true);
            return PiGameViewPhysicsRaycast.ToJson(result);
        }

        /// <summary>
        /// Pre-click probe: UI EventSystem hit + optional 3D physics, same coordinate path as input_click.
        /// </summary>
        public static string ProbeJson(
            float x,
            float y,
            float maxDistance = PiGameViewPhysicsRaycast.DefaultMaxDistance,
            int layerMask = Physics.DefaultRaycastLayers,
            bool includePhysics = true)
        {
            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError) ||
                !TryValidateFinite("max_distance", maxDistance, out numberError))
                return Fail(numberError, "usage");

            var result = PiInputProbe.ProbeAt(x, y, maxDistance, layerMask, includePhysics);
            return PiInputProbe.ToJson(result);
        }

        static HarnessInput()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
                ResetSplitDragState();
        }

        public static string GetReadyStateJson()
        {
            ReadyState state = CaptureReadyState(0, 0);
            return ReadyStateToJson(state, "succeeded", null, null);
        }

        public static IEnumerator WaitForReadyJson(int minFrames = 1, int timeoutMs = 3000)
        {
            if (minFrames < 1) minFrames = 1;
            if (timeoutMs <= 0) timeoutMs = 3000;

            double start = EditorApplication.timeSinceStartup;
            double deadline = start + timeoutMs / 1000.0;
            int stableFrames = 0;
            ReadyState state = CaptureReadyState(0, 0);

            while (EditorApplication.timeSinceStartup <= deadline)
            {
                state = CaptureReadyState(stableFrames, ElapsedMs(start));
                if (state.Ready)
                {
                    if (stableFrames >= minFrames)
                    {
                        PrepareBackendForInput();
                        state = CaptureReadyState(stableFrames, ElapsedMs(start));
                        yield return ReadyStateToJson(state, "succeeded", null, null);
                        yield break;
                    }

                    stableFrames++;
                }
                else
                {
                    stableFrames = 0;
                }

                yield return null;
            }

            state = CaptureReadyState(stableFrames, ElapsedMs(start));
            yield return ReadyStateToJson(state, "failed",
                "Input did not become ready before timeout.", "timeout");
        }

        private static bool EnsureReady(out string errorJson)
        {
            if (!IsAvailable)
            {
                errorJson = Fail("com.unity.inputsystem is not installed.", "not_supported");
                return false;
            }

            if (!EditorApplication.isPlaying)
            {
                errorJson = Fail("Input injection requires PlayMode.", "not_supported");
                return false;
            }

            errorJson = null;
            return true;
        }

        private static ReadyState CaptureReadyState(int stableFrames, long elapsedMs)
        {
            var state = new ReadyState
            {
                InputSystem = IsAvailable,
                Playing = EditorApplication.isPlaying,
                StableFrames = stableFrames,
                ElapsedMs = elapsedMs
            };

            if (state.InputSystem)
            {
                state.Keyboard = InvokeBackendBool("HasKeyboard");
                state.Mouse = InvokeBackendBool("HasMouse");
            }

            if (!state.InputSystem)
            {
                state.Reason = "input_system_missing";
                state.ErrorType = "not_supported";
            }
            else if (!state.Playing)
            {
                state.Reason = "not_playing";
                state.ErrorType = "not_supported";
            }
            else if (!state.Keyboard)
            {
                state.Reason = "keyboard_unavailable";
                state.ErrorType = "not_supported";
            }
            else if (!state.Mouse)
            {
                state.Reason = "mouse_unavailable";
                state.ErrorType = "not_supported";
            }
            else
            {
                state.Ready = true;
                state.Reason = "ready";
            }

            return state;
        }

        private static string ReadyStateToJson(ReadyState state, string status,
            string error, string errorType)
        {
            string resolvedErrorType = errorType ?? state.ErrorType;
            var json = InputJson.Object()
                .String("status", status)
                .Bool("ready", state.Ready)
                .Bool("input_system", state.InputSystem)
                .Bool("playing", state.Playing)
                .Bool("keyboard", state.Keyboard)
                .Bool("mouse", state.Mouse)
                .Number("stable_frames", state.StableFrames)
                .Number("elapsed_ms", state.ElapsedMs)
                .String("reason", state.Reason);

            if (!string.IsNullOrEmpty(error))
                json.String("error", error);
            if (!string.IsNullOrEmpty(resolvedErrorType))
                json.String("error_type", resolvedErrorType);

            return json.ToString();
        }

        private static Type BackendType => Type.GetType(BackendTypeName, false);

        private static void PrepareBackendForInput()
        {
            try
            {
                InvokeBackendVoid("PrepareForInput");
            }
            catch (Exception ex)
            {
                // Ready probing should not fail just because GameView focus/routing setup failed.
                UnityEngine.Debug.LogWarning("[Harness.Input] PrepareForInput failed: " + ex.Message);
            }
        }

        private static long ElapsedMs(double start)
            => (long)((EditorApplication.timeSinceStartup - start) * 1000.0);

        private struct ReadyState
        {
            public bool InputSystem;
            public bool Playing;
            public bool Keyboard;
            public bool Mouse;
            public bool Ready;
            public int StableFrames;
            public long ElapsedMs;
            public string Reason;
            public string ErrorType;
        }
    }
}
