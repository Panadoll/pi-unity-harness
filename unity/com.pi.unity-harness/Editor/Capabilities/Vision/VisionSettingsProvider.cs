using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    internal sealed class VisionSettingsProvider : SettingsProvider
    {
        private static string s_pendingApiKey = "";

        public VisionSettingsProvider()
            : base("Project/Harness/Vision", SettingsScope.Project) { }

        [SettingsProvider]
        public static SettingsProvider Create() => new VisionSettingsProvider();

        public override void OnGUI(string searchContext)
        {
            var settings = VisionSettings.Instance;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Vision Analysis", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Default provider is none. Enable openai-compatible only when you want Unity to send screenshots to the configured endpoint.",
                MessageType.Info);

            string provider = settings.ProviderNormalized;
            int providerIndex = provider == "openai-compatible" ? 1 : 0;
            int newProviderIndex = EditorGUILayout.Popup(
                new GUIContent("Provider"),
                providerIndex,
                new[] { "none", "openai-compatible" });
            string newProvider = newProviderIndex == 1 ? "openai-compatible" : "none";
            if (newProvider != settings.provider)
            {
                settings.provider = newProvider;
                settings.Save();
            }

            DrawPresets(settings);

            using (new EditorGUI.DisabledScope(settings.ProviderNormalized != "openai-compatible"))
            {
                DrawOpenAiCompatible(settings);
            }
        }

        private static void DrawPresets(VisionSettings settings)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("OpenAI"))
                    settings.ApplyOpenAiPreset();
                if (GUILayout.Button("MiMo"))
                    settings.ApplyMiMoPreset();
                if (GUILayout.Button("Local"))
                    settings.ApplyLocalPreset();
            }
        }

        private static void DrawOpenAiCompatible(VisionSettings settings)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("OpenAI-Compatible Provider", EditorStyles.boldLabel);

            string newBaseUrl = EditorGUILayout.TextField(
                new GUIContent("Base URL", "Examples: https://api.openai.com, https://api.xiaomimimo.com, http://localhost:1234"),
                settings.openAiBaseUrl ?? "");
            SaveIfChanged(settings, ref settings.openAiBaseUrl, newBaseUrl);

            EditorGUILayout.SelectableLabel(
                "Resolved endpoint: " + settings.BuildOpenAiEndpoint(),
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            string newModel = EditorGUILayout.TextField(
                new GUIContent("Model", "Examples: gpt-4o-mini, mimo-v2.5, qwen2.5-vl"),
                settings.openAiModel ?? "");
            SaveIfChanged(settings, ref settings.openAiModel, newModel);

            string storedKey = settings.StoredOpenAiApiKey;
            bool hasKey = !string.IsNullOrWhiteSpace(settings.OpenAiApiKey);
            string keyStatus = hasKey
                ? "API key configured via " + settings.OpenAiApiKeySource + (string.IsNullOrEmpty(storedKey) ? "" : " (" + MaskKey(storedKey) + ")")
                : "API key missing";
            EditorGUILayout.LabelField(
                new GUIContent("API Key", "Stored in EditorPrefs for this project, not in ProjectSettings."),
                new GUIContent(keyStatus));
            s_pendingApiKey = EditorGUILayout.PasswordField(
                new GUIContent("New API Key", "Paste a new key here, then click Save API Key."),
                s_pendingApiKey);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(s_pendingApiKey)))
                {
                    if (GUILayout.Button("Save API Key", GUILayout.MaxWidth(120)))
                    {
                        settings.StoredOpenAiApiKey = s_pendingApiKey;
                        s_pendingApiKey = "";
                    }
                }

                if (GUILayout.Button("Clear API Key", GUILayout.MaxWidth(120)))
                {
                    settings.StoredOpenAiApiKey = "";
                    s_pendingApiKey = "";
                }
            }

            string newHeader = EditorGUILayout.TextField(
                new GUIContent("API Key Header", "OpenAI uses Authorization. MiMo uses api-key."),
                settings.openAiApiKeyHeader ?? "");
            SaveIfChanged(settings, ref settings.openAiApiKeyHeader, newHeader);

            int authIndex = AuthSchemeIndex(settings.OpenAiAuthSchemeNormalized);
            int newAuthIndex = EditorGUILayout.Popup(
                new GUIContent("Auth Scheme", "bearer => Header: Bearer key; raw => Header: key; none => no auth header"),
                authIndex,
                new[] { "bearer", "raw", "none" });
            string newAuth = newAuthIndex == 2 ? "none" : newAuthIndex == 1 ? "raw" : "bearer";
            SaveIfChanged(settings, ref settings.openAiAuthScheme, newAuth);

            int newTimeout = EditorGUILayout.IntField(
                new GUIContent("Timeout Ms"),
                settings.openAiTimeoutMs);
            if (newTimeout != settings.openAiTimeoutMs)
            {
                settings.openAiTimeoutMs = Mathf.Clamp(newTimeout, 1000, 300000);
                settings.Save();
            }

            int newTokens = EditorGUILayout.IntField(
                new GUIContent("Max Completion Tokens"),
                settings.openAiMaxCompletionTokens);
            if (newTokens != settings.openAiMaxCompletionTokens)
            {
                settings.openAiMaxCompletionTokens = Mathf.Clamp(newTokens, 1, 32768);
                settings.Save();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Test Vision Provider", GUILayout.MaxWidth(180)))
                    RunProviderTest();
                EditorGUILayout.LabelField("Runs a text-only chat/completions check; no screenshot is uploaded.");
            }

            EditorGUILayout.HelpBox(
                "MiMo preset: Base URL https://api.xiaomimimo.com, Model mimo-v2.5, API Key Header api-key, Auth Scheme raw.",
                MessageType.None);
        }

        private static void SaveIfChanged(VisionSettings settings, ref string field, string value)
        {
            if (value == field) return;
            field = value;
            settings.Save();
        }

        private static int AuthSchemeIndex(string scheme)
        {
            if (scheme == "raw") return 1;
            if (scheme == "none") return 2;
            return 0;
        }

        private static async void RunProviderTest()
        {
            string result = await OpenAiCompatibleVisionAnalyzer.TestProviderAsync();
            bool ok = result.Contains("\"status\":\"succeeded\"");
            Debug.Log("[Harness.Vision] Provider test result: " + result);
            EditorUtility.DisplayDialog(
                ok ? "Harness Vision Provider Test Succeeded" : "Harness Vision Provider Test Failed",
                result,
                "OK");
        }

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 8) return new string('*', key.Length);
            return key.Substring(0, 4) + new string('*', key.Length - 8) + key.Substring(key.Length - 4);
        }
    }
}
