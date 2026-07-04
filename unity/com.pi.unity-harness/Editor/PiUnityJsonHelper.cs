using System.Text;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 跨模块共享的 JSON 手工序列化辅助函数。
    /// 避免在 PiUnityBridge、PiUnityPipelineCommandExecutor、PiUnityTestCoordinator 中重复定义。
    /// </summary>
    internal static class PiUnityJsonHelper
    {
        /// <summary>
        /// 构建 ok=false 的错误响应 JSON 字符串。
        /// </summary>
        public static string ErrorJson(string replyTo, string errorType, string error)
        {
            return "{\"reply_to\":" + JsonString(replyTo) + ",\"ok\":false,\"error_type\":" + JsonString(errorType) + ",\"error\":" + JsonString(error) + "}";
        }

        /// <summary>
        /// 构建 ok=true 的成功响应 JSON 字符串，result 已为 JSON 原文（不加外层引号）。
        /// </summary>
        public static string SuccessJson(string replyTo, string rawResultJson)
        {
            return "{\"reply_to\":" + JsonString(replyTo) + ",\"ok\":true,\"result\":" + rawResultJson + "}";
        }

        /// <summary>
        /// JSON 字符串转义并加双引号包裹。null 返回字面量 null。
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
        /// JSON 字符串值内容转义（不加外层双引号）。用于拼入已存在的 JSON 字符串中。
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
