using System.Text;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Capabilities.UiTree
{
    /// <summary>
    /// Lightweight JSON writer for UI tree output. No external dependencies.
    /// Follows the same hand-written JSON pattern used by HarnessInput.
    /// </summary>
    internal static class UiTreeJson
    {
        internal static string BoolStr(bool v) => v ? "true" : "false";

        internal static string FloatStr(float v)
            => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal static string IntStr(int v) => v.ToString();

        internal static void AppendStringArray(StringBuilder sb, string[] arr)
        {
            if (arr == null || arr.Length == 0)
            {
                sb.Append("[]");
                return;
            }

            AppendStringArray(sb, (System.Collections.Generic.IEnumerable<string>)arr);
        }

        internal static void AppendStringArray(StringBuilder sb, System.Collections.Generic.IEnumerable<string> values)
        {
            sb.Append("[");
            bool needsComma = false;
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (needsComma) sb.Append(",");
                    sb.Append("\"").Append(PiUnityJsonHelper.EscapeJson(value)).Append("\"");
                    needsComma = true;
                }
            }
            sb.Append("]");
        }

        /// <summary>
        /// Build a JSON object for the standard truncation metadata.
        /// </summary>
        internal static void AppendTruncationMeta(StringBuilder sb, int totalCount,
            int matchedCount, int returnedCount, int maxDepth, int limit)
        {
            sb.Append(",");
            sb.Append("\"total_count\":").Append(totalCount);
            sb.Append(",\"matched_count\":").Append(matchedCount);
            sb.Append(",\"returned_count\":").Append(returnedCount);
            sb.Append(",\"truncated\":").Append(BoolStr(returnedCount < matchedCount));
            sb.Append(",\"omitted_count\":").Append(System.Math.Max(0, matchedCount - returnedCount));
            sb.Append(",\"max_depth\":").Append(maxDepth);
            sb.Append(",\"limit\":").Append(limit);
        }

        internal static void AppendInputHint(StringBuilder sb, string action,
            bool includeCoordinates, float x, float y, string coordinateSpace,
            string source, bool requiresFocus, string targetRef, string targetName,
            string targetType, string button = null,
            float gameViewWidth = 0f, float gameViewHeight = 0f,
            string[] handlers = null, float dragDistance = 0f)
        {
            sb.Append(",\"input_hint\":{\"action\":\"").Append(PiUnityJsonHelper.EscapeJson(action)).Append("\"");

            if (includeCoordinates)
            {
                sb.Append(",\"x\":").Append(FloatStr(x));
                sb.Append(",\"y\":").Append(FloatStr(y));
            }

            if (!string.IsNullOrEmpty(button))
                sb.Append(",\"button\":\"").Append(PiUnityJsonHelper.EscapeJson(button)).Append("\"");

            sb.Append(",\"coordinate_space\":\"").Append(PiUnityJsonHelper.EscapeJson(coordinateSpace)).Append("\"");
            if (gameViewWidth > 0f && gameViewHeight > 0f)
            {
                sb.Append(",\"gameview_size\":{\"w\":").Append(FloatStr(gameViewWidth));
                sb.Append(",\"h\":").Append(FloatStr(gameViewHeight)).Append("}");
            }
            if (handlers != null && handlers.Length > 0)
            {
                sb.Append(",\"handlers\":");
                AppendStringArray(sb, handlers);
            }
            if (action == "drag" && includeCoordinates && dragDistance > 0f)
            {
                AppendSafeDrag(sb, "safe_drag_up", x, y, x, y - dragDistance, button);
                AppendSafeDrag(sb, "safe_drag_down", x, y, x, y + dragDistance, button);
                AppendSafeDrag(sb, "safe_drag_left", x, y, x - dragDistance, y, button);
                AppendSafeDrag(sb, "safe_drag_right", x, y, x + dragDistance, y, button);
            }
            sb.Append(",\"source\":\"").Append(PiUnityJsonHelper.EscapeJson(source)).Append("\"");
            sb.Append(",\"requires_focus\":").Append(BoolStr(requiresFocus));
            sb.Append(",\"target_ref\":\"").Append(PiUnityJsonHelper.EscapeJson(targetRef)).Append("\"");
            sb.Append(",\"target_name\":\"").Append(PiUnityJsonHelper.EscapeJson(targetName)).Append("\"");
            sb.Append(",\"target_type\":\"").Append(PiUnityJsonHelper.EscapeJson(targetType)).Append("\"");
            sb.Append("}");
        }

        private static void AppendSafeDrag(StringBuilder sb, string name,
            float fromX, float fromY, float toX, float toY, string button)
        {
            sb.Append(",\"").Append(name).Append("\":{");
            sb.Append("\"from_x\":").Append(FloatStr(fromX));
            sb.Append(",\"from_y\":").Append(FloatStr(fromY));
            sb.Append(",\"to_x\":").Append(FloatStr(toX));
            sb.Append(",\"to_y\":").Append(FloatStr(toY));
            sb.Append(",\"button\":\"").Append(PiUnityJsonHelper.EscapeJson(string.IsNullOrEmpty(button) ? "left" : button)).Append("\"");
            sb.Append(",\"steps\":15}");
        }

        internal static void AppendInputTemplate(StringBuilder sb, string interaction,
            float x, float y, bool hasValueRange,
            float currentValue, float minValue, float maxValue, bool wholeNumbers,
            string button = "left")
        {
            if (interaction == "type_text")
            {
                sb.Append(",\"input_template\":{\"executable\":false");
                sb.Append(",\"kind\":\"fill_text\"");
                sb.Append(",\"parameters\":[\"text\"]");
                sb.Append(",\"actions\":[");
                sb.Append("{\"type\":\"click\",\"x\":").Append(FloatStr(x));
                sb.Append(",\"y\":").Append(FloatStr(y));
                sb.Append(",\"button\":\"").Append(PiUnityJsonHelper.EscapeJson(button)).Append("\"},");
                sb.Append("{\"type\":\"type_text\",\"text\":\"__USER_INPUT__\"}");
                sb.Append("]}");
                return;
            }

            if (interaction == "drag")
            {
                sb.Append(",\"input_template\":{\"executable\":false");
                if (hasValueRange)
                {
                    sb.Append(",\"kind\":\"set_value\"");
                    sb.Append(",\"parameters\":[\"value\"]");
                    sb.Append(",\"value_range\":{\"current\":").Append(FloatStr(currentValue));
                    sb.Append(",\"min\":").Append(FloatStr(minValue));
                    sb.Append(",\"max\":").Append(FloatStr(maxValue));
                    sb.Append(",\"whole_numbers\":").Append(BoolStr(wholeNumbers)).Append("}");
                }
                else
                {
                    sb.Append(",\"kind\":\"drag_to_point\"");
                    sb.Append(",\"parameters\":[\"to_x\",\"to_y\"]");
                }
                sb.Append(",\"actions\":[");
                sb.Append("{\"type\":\"drag\",\"from_x\":").Append(FloatStr(x));
                sb.Append(",\"from_y\":").Append(FloatStr(y));
                if (hasValueRange)
                {
                    sb.Append(",\"to_x\":\"__VALUE_TO_X__\",\"to_y\":").Append(FloatStr(y));
                }
                else
                {
                    sb.Append(",\"to_x\":\"__TARGET_X__\",\"to_y\":\"__TARGET_Y__\"");
                }
                sb.Append(",\"button\":\"").Append(PiUnityJsonHelper.EscapeJson(button)).Append("\"}");
                sb.Append("]}");
                return;
            }

            if (interaction == "scroll")
            {
                sb.Append(",\"input_template\":{\"executable\":false");
                sb.Append(",\"kind\":\"scroll_delta\"");
                sb.Append(",\"parameters\":[\"delta_x\",\"delta_y\"]");
                sb.Append(",\"actions\":[");
                sb.Append("{\"type\":\"scroll\",\"x\":").Append(FloatStr(x));
                sb.Append(",\"y\":").Append(FloatStr(y));
                sb.Append(",\"delta_x\":\"__DELTA_X__\",\"delta_y\":\"__DELTA_Y__\"}");
                sb.Append("]}");
            }
        }

        /// <summary>
        /// Returns a standard error JSON string.
        /// </summary>
        internal static string Error(string message, string errorType)
        {
            return "{\"status\":\"failed\",\"error\":\"" + PiUnityJsonHelper.EscapeJson(message) +
                   "\",\"error_type\":\"" + PiUnityJsonHelper.EscapeJson(errorType) + "\"}";
        }
    }
}
