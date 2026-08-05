using System;
using System.Collections;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    public static partial class HarnessInput
    {
        public static string PressKeyJson(string key)
        {
            if (!EnsureReady(out string errorJson))
                return errorJson;

            return InvokeBackend("PressKey", new object[] { key }, () => Success("press_key", "key", key));
        }

        public static string ReleaseKeyJson(string key)
        {
            if (!EnsureReady(out string errorJson))
                return errorJson;

            return InvokeBackend("ReleaseKey", new object[] { key }, () => Success("release_key", "key", key));
        }

        public static string PressMouseButtonJson(string button)
        {
            if (!EnsureReady(out string errorJson))
                return errorJson;

            if (!TryParseMouseButton(button, out int parsedButton, out string error))
                return Fail(error, "usage");

            try
            {
                InvokeBackendVoid("PressMouseButton", parsedButton);
                return Success("press_mouse_button", "button", NormalizeMouseButton(parsedButton));
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        public static string ReleaseMouseButtonJson(string button)
        {
            if (!EnsureReady(out string errorJson))
                return errorJson;

            if (!TryParseMouseButton(button, out int parsedButton, out string error))
                return Fail(error, "usage");

            try
            {
                InvokeBackendVoid("ReleaseMouseButton", parsedButton);
                return Success("release_mouse_button", "button", NormalizeMouseButton(parsedButton));
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        public static string SetMousePositionJson(float x, float y)
        {
            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError))
                return Fail(numberError, "usage");

            if (!EnsureReady(out string errorJson))
                return errorJson;

            try
            {
                InvokeBackendVoid("SetMousePosition", x, y);
                return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "set_mouse_position")
                    .Number("x", x)
                    .Number("y", y)
                    .ToString();
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        public static string ClearAllInputJson()
        {
            if (!EnsureReady(out string errorJson))
                return errorJson;

            try
            {
                ResetSplitDragState();
                InvokeBackendVoid("ClearAllInput");
                return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "clear_all_input")
                    .ToString();
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        // =========================================================
        // Action-level APIs (Level 1 enhancement)
        // =========================================================

        public static IEnumerator ClickJson(float x, float y, string button = "left")
        {
            if (!TryParseMouseButton(button, out int parsedButton, out string error))
            {
                yield return Fail(error, "usage");
                yield break;
            }

            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError))
            {
                yield return Fail(numberError, "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            string normalizedButton = NormalizeMouseButton(parsedButton);
            try
            {
                if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, x, y))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // let position settle
                if (!TryInvokeBackendVoid("PressMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // hold for one frame
                if (!TryInvokeBackendVoid("ReleaseMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // let UI process the release event

                yield return BuildPointerDispatchResult(
                    "click", x, y, normalizedButton, GetDispatchResultJson(),
                    "EventSystem dispatch did not reach a click handler");
            }
            finally
            {
                ReleaseMouseButtonBestEffort(parsedButton);
            }
        }

        public static IEnumerator DoubleClickJson(float x, float y, string button = "left")
        {
            if (!TryParseMouseButton(button, out int parsedButton, out string error))
            {
                yield return Fail(error, "usage");
                yield break;
            }

            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError))
            {
                yield return Fail(numberError, "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            string normalizedButton = NormalizeMouseButton(parsedButton);
            try
            {
                if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, x, y))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
                // First click
                if (!TryInvokeBackendVoid("PressMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
                if (!TryInvokeBackendVoid("ReleaseMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
                // Second click (same frame gap for MultiTap detection)
                if (!TryInvokeBackendVoid("PressMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
                if (!TryInvokeBackendVoid("ReleaseMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // let UI process the second release event

                yield return BuildPointerDispatchResult(
                    "double_click", x, y, normalizedButton, GetDispatchResultJson(),
                    "EventSystem dispatch did not reach a click handler");
            }
            finally
            {
                ReleaseMouseButtonBestEffort(parsedButton);
            }
        }

        public static IEnumerator DragJson(float fromX, float fromY, float toX, float toY,
            string button = "left", int steps = 0)
        {
            if (!TryParseMouseButton(button, out int parsedButton, out string error))
            {
                yield return Fail(error, "usage");
                yield break;
            }

            if (!TryValidateFinite("from_x", fromX, out string numberError) ||
                !TryValidateFinite("from_y", fromY, out numberError) ||
                !TryValidateFinite("to_x", toX, out numberError) ||
                !TryValidateFinite("to_y", toY, out numberError))
            {
                yield return Fail(numberError, "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            if (steps < 3) steps = 10; // minimum interpolation density
            string normalizedButton = NormalizeMouseButton(parsedButton);
            try
            {
                if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, fromX, fromY))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
                if (!TryInvokeBackendVoid("PressMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;

                for (int i = 1; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    float cx = fromX + (toX - fromX) * t;
                    float cy = fromY + (toY - fromY) * t;
                    if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, cx, cy))
                    {
                        yield return errorJson;
                        yield break;
                    }
                    yield return null;
                }

                if (!TryInvokeBackendVoid("ReleaseMouseButton", out errorJson, parsedButton))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // let drop handlers process the release

                string dispatchJson = GetDispatchResultJson();
                yield return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "drag")
                    .Number("from_x", fromX)
                    .Number("from_y", fromY)
                    .Number("to_x", toX)
                    .Number("to_y", toY)
                    .String("button", normalizedButton)
                    .Number("steps", steps)
                    .Raw("dispatch", dispatchJson)
                    .ToString();
            }
            finally
            {
                ReleaseMouseButtonBestEffort(parsedButton);
            }
        }

        public static IEnumerator ScrollJson(float x, float y, float deltaX, float deltaY)
        {
            if (!TryValidateFinite("x", x, out string numberError) ||
                !TryValidateFinite("y", y, out numberError) ||
                !TryValidateFinite("delta_x", deltaX, out numberError) ||
                !TryValidateFinite("delta_y", deltaY, out numberError))
            {
                yield return Fail(numberError, "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, x, y))
            {
                yield return errorJson;
                yield break;
            }
            yield return null; // let pointer position settle before scrolling

            if (!TryInvokeBackendVoid("SetScrollDelta", out errorJson, deltaX, deltaY))
            {
                yield return errorJson;
                yield break;
            }
            yield return null; // let ScrollRect consume the wheel delta

            yield return BuildScrollDispatchResult(x, y, deltaX, deltaY, GetDispatchResultJson());
        }

        public static IEnumerator TypeTextJson(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield return Fail("text is required.", "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (!TryInvokeBackendVoid("QueueTextInput", out errorJson, text[i]))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null; // one frame per character
            }

            yield return InputJson.Object()
                .String("status", "succeeded")
                .String("action", "type_text")
                .Number("length", text.Length)
                .ToString();
        }

        public static IEnumerator KeyChordJson(string keysJson)
        {
            string[] keys = ParseSimpleStringArray(keysJson);
            if (keys == null || keys.Length == 0)
            {
                yield return Fail("keysJson must be a JSON array of key names, e.g. [\"LeftCtrl\",\"S\"].", "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            int pressedCount = 0;
            try
            {
                // Press all keys in order
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!TryInvokeBackendVoid("PressKey", out errorJson, keys[i]))
                    {
                        yield return errorJson;
                        yield break;
                    }
                    pressedCount++;
                }
                yield return null; // hold for one frame

                // Release in reverse order
                for (int i = keys.Length - 1; i >= 0; i--)
                {
                    if (!TryInvokeBackendVoid("ReleaseKey", out errorJson, keys[i]))
                    {
                        yield return errorJson;
                        yield break;
                    }
                }

                yield return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "key_chord")
                    .StringArray("keys", keys)
                    .ToString();
            }
            finally
            {
                // Release all keys that were pressed (best-effort)
                for (int i = pressedCount - 1; i >= 0; i--)
                {
                    try { InvokeBackendVoid("ReleaseKey", new object[] { keys[i] }); }
                    catch { /* best-effort */ }
                }
            }
        }
    }
}