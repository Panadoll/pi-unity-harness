namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    internal static class PiAbilityJson
    {
        public static string Escape(string value)
        {
            return PiUnityJsonHelper.EscapeJson(value);
        }
    }
}
