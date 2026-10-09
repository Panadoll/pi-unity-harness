using Pi.UnityHarness.Editor.Capabilities.Shared;

namespace Pi.UnityHarness.Editor.Protocol
{
    /// <summary>
    /// 管道响应信封。只写 reply_to / ok / result / error_type / error，不写 mux id 或 CLI 合成字段。
    /// result 必须已经是原始 JSON。
    /// </summary>
    internal static class PipeEnvelope
    {
        public static string Error(string replyTo, string errorType, string error)
        {
            return "{\"reply_to\":" + JsonText.Quote(replyTo) + ",\"ok\":false,\"error_type\":" + JsonText.Quote(errorType) + ",\"error\":" + JsonText.Quote(error) + "}";
        }

        public static string Success(string replyTo, string rawResultJson)
        {
            return "{\"reply_to\":" + JsonText.Quote(replyTo) + ",\"ok\":true,\"result\":" + rawResultJson + "}";
        }
    }
}
