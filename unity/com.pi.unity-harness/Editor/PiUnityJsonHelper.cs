using System.Text;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Hand-rolled JSON serialization helpers shared across modules.
    /// Avoids duplicating them in PiUnityBridge, PiUnityPipelineCommandExecutor, and PiUnityTestCoordinator.
    /// </summary>
    internal static class PiUnityJsonHelper
    {
        /// <summary>
        /// Builds an ok=false error response JSON string.
        /// </summary>
        public static string ErrorJson(string replyTo, string errorType, string error)
        {
            return "{\"reply_to\":" + JsonString(replyTo) + ",\"ok\":false,\"error_type\":" + JsonString(errorType) + ",\"error\":" + JsonString(error) + "}";
        }

        /// <summary>
        /// Builds an ok=true success response JSON string; result is already raw JSON (no outer quotes).
        /// </summary>
        public static string SuccessJson(string replyTo, string rawResultJson)
        {
            return "{\"reply_to\":" + JsonString(replyTo) + ",\"ok\":true,\"result\":" + rawResultJson + "}";
        }

        /// <summary>
        /// Escapes a JSON string and wraps it in double quotes. null returns the literal null.
        /// </summary>
        public static string JsonString(string value)
        {
            if (value == null)
                return "null";

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            AppendEscaped(sb, value);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Escapes the content of a JSON string value (no outer quotes). For splicing into an existing JSON string.
        /// </summary>
        public static string EscapeJson(string value)
        {
            if (value == null)
                return "";

            StringBuilder sb = new StringBuilder(value.Length);
            AppendEscaped(sb, value);
            return sb.ToString();
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
