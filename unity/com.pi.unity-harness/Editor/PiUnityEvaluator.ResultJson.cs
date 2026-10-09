using Pi.UnityHarness.Editor.Capabilities.Shared;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// eval 领域结果与管道信封的一次拼接。timingFragment 必须已带前导逗号，或为空。
    /// </summary>
    internal static class PiUnityEvalResultJson
    {
        internal static string Build(string replyTo, string output, string typeName, string timingFragment = "")
        {
            return "{\"reply_to\":" + JsonText.Quote(replyTo) +
                   ",\"ok\":true,\"result\":{\"output\":" + JsonText.Quote(output) +
                   ",\"typeName\":" + JsonText.Quote(typeName) + timingFragment + "}}";
        }
    }
}
