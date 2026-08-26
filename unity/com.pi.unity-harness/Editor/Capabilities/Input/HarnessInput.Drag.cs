using System;
using System.Collections;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    public static partial class HarnessInput
    {
        private static bool s_splitDragActive;
        private static int s_splitDragButton = -1;
        private static float s_splitDragX;
        private static float s_splitDragY;

        public static IEnumerator DragStartJson(float x, float y, string button = "left")
        {
            if (s_splitDragActive)
            {
                yield return Fail("A drag is already in progress. Call DragEndJson first.", "runtime");
                yield break;
            }

            if (!TryParseMouseButton(button, out int parsedButton, out string error))
            {
                yield return Fail(error, "usage");
                yield break;
            }

            if (parsedButton != 0)
            {
                yield return Fail("Drag actions only support left mouse button.", "usage");
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

            if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, x, y))
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

            s_splitDragActive = true;
            s_splitDragButton = parsedButton;
            s_splitDragX = x;
            s_splitDragY = y;

            string dispatchJson = GetDispatchResultJson();
            bool dispatched = dispatchJson.Contains("\"dispatched\":true");
            if (!dispatched)
            {
                ResetSplitDragState();
                ReleaseMouseButtonBestEffort(parsedButton);

                yield return InputJson.Object()
                    .String("status", "failed")
                    .String("action", "drag_start")
                    .Number("x", x)
                    .Number("y", y)
                    .String("button", NormalizeMouseButton(parsedButton))
                    .String("error", "EventSystem dispatch did not reach a pointer handler")
                    .String("error_type", "runtime")
                    .Raw("dispatch", dispatchJson)
                    .ToString();
                yield break;
            }

            yield return InputJson.Object()
                .String("status", "succeeded")
                .String("action", "drag_start")
                .Number("x", x)
                .Number("y", y)
                .String("button", NormalizeMouseButton(parsedButton))
                .Raw("dispatch", dispatchJson)
                .ToString();
        }

        public static IEnumerator DragMoveJson(float x, float y, int steps = 0)
        {
            if (!s_splitDragActive)
            {
                yield return Fail("No drag in progress. Call DragStartJson first.", "runtime");
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

            if (steps < 1) steps = 1;
            float fromX = s_splitDragX;
            float fromY = s_splitDragY;

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float cx = fromX + (x - fromX) * t;
                float cy = fromY + (y - fromY) * t;
                if (!TryInvokeBackendVoid("SetMousePosition", out errorJson, cx, cy))
                {
                    yield return errorJson;
                    yield break;
                }
                yield return null;
            }

            s_splitDragX = x;
            s_splitDragY = y;

            yield return InputJson.Object()
                .String("status", "succeeded")
                .String("action", "drag_move")
                .Number("x", x)
                .Number("y", y)
                .Number("steps", steps)
                .Raw("dispatch", GetDispatchResultJson())
                .ToString();
        }

        public static IEnumerator DragEndJson(float x, float y, int steps = 0)
        {
            if (!s_splitDragActive)
            {
                yield return Fail("No drag in progress. Call DragStartJson first.", "runtime");
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

            if (steps < 1) steps = 1;
            int parsedButton = s_splitDragButton;
            float fromX = s_splitDragX;
            float fromY = s_splitDragY;

            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    float cx = fromX + (x - fromX) * t;
                    float cy = fromY + (y - fromY) * t;
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
                yield return null;

                yield return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "drag_end")
                    .Number("x", x)
                    .Number("y", y)
                    .Number("steps", steps)
                    .Raw("dispatch", GetDispatchResultJson())
                    .ToString();
            }
            finally
            {
                ResetSplitDragState();
                ReleaseMouseButtonBestEffort(parsedButton);
            }
        }

        public static string CancelDragJson()
        {
            if (!s_splitDragActive)
                return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "cancel_drag")
                    .Bool("was_dragging", false)
                    .ToString();

            int parsedButton = s_splitDragButton;
            ResetSplitDragState();

            try
            {
                InvokeBackendVoid("ReleaseMouseButton", parsedButton);
                return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "cancel_drag")
                    .Bool("was_dragging", true)
                    .Raw("dispatch", GetDispatchResultJson())
                    .ToString();
            }
            catch (Exception ex)
            {
                return ExceptionToJson(ex);
            }
        }

        private static void ResetSplitDragState()
        {
            s_splitDragActive = false;
            s_splitDragButton = -1;
            s_splitDragX = 0f;
            s_splitDragY = 0f;
        }
    }
}