using System.Text;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// 手写 JSON 的字符串与字段原语。null 的 Quote 输出字面 null；Escape 只转义内容，null 变成空串。
    /// 字段集合仍由各领域 builder 决定。
    /// </summary>
    internal static class JsonText
    {
        public static string Quote(string value)
        {
            if (value == null)
                return "null";

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            AppendEscaped(sb, value);
            sb.Append('"');
            return sb.ToString();
        }

        public static string Escape(string value)
        {
            if (value == null)
                return "";

            StringBuilder sb = new StringBuilder(value.Length);
            AppendEscaped(sb, value);
            return sb.ToString();
        }

        /// <summary>
        /// 追加 ,"name":value。数字用不变区域性；字符串转义，null 写成 null。
        /// </summary>
        public static void AppendString(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(name).Append("\":");
            if (value == null)
                sb.Append("null");
            else
                sb.Append('"').Append(Escape(value)).Append('"');
        }

        public static void AppendBool(StringBuilder sb, string name, bool value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        }

        public static void AppendNumber(StringBuilder sb, string name, float value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":")
                .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public static void AppendNumber(StringBuilder sb, string name, int value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":").Append(value);
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
