using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    /// <summary>
    /// Project-level vision settings. Non-secret options are stored in ProjectSettings;
    /// API keys are kept in EditorPrefs so they are not committed with the project.
    /// </summary>
    [Serializable]
    internal class VisionSettings
    {
        private const string SettingsPath = "ProjectSettings/HarnessVisionSettings.json";
        private const string DefaultOpenAiBaseUrl = "https://api.openai.com";
        private const string DefaultOpenAiModel = "gpt-4o-mini";

        public string provider = "none";
        public string openAiBaseUrl = DefaultOpenAiBaseUrl;
        public string openAiModel = DefaultOpenAiModel;
        public string openAiApiKeyHeader = "Authorization";
        public string openAiAuthScheme = "bearer";
        public int openAiTimeoutMs = 30000;
        public int openAiMaxCompletionTokens = 1024;

        private static VisionSettings s_instance;

        public static VisionSettings Instance
        {
            get
            {
                if (s_instance == null) s_instance = Load();
                return s_instance;
            }
        }

        public string ProviderNormalized => string.IsNullOrWhiteSpace(provider)
            ? "none"
            : provider.Trim().ToLowerInvariant();

        public string OpenAiBaseUrlOrDefault => string.IsNullOrWhiteSpace(openAiBaseUrl)
            ? DefaultOpenAiBaseUrl
            : openAiBaseUrl.Trim();

        public string OpenAiModelOrDefault => string.IsNullOrWhiteSpace(openAiModel)
            ? DefaultOpenAiModel
            : openAiModel.Trim();

        public string OpenAiApiKeyHeaderOrDefault => string.IsNullOrWhiteSpace(openAiApiKeyHeader)
            ? "Authorization"
            : openAiApiKeyHeader.Trim();

        public string OpenAiAuthSchemeNormalized => string.IsNullOrWhiteSpace(openAiAuthScheme)
            ? "bearer"
            : openAiAuthScheme.Trim().ToLowerInvariant();

        public int OpenAiTimeoutMsClamped => Mathf.Clamp(openAiTimeoutMs <= 0 ? 30000 : openAiTimeoutMs, 1000, 300000);

        public int OpenAiMaxCompletionTokensClamped => Mathf.Clamp(
            openAiMaxCompletionTokens <= 0 ? 1024 : openAiMaxCompletionTokens,
            1,
            32768);

        public string OpenAiApiKey
        {
            get
            {
                string stored = EditorPrefs.GetString(OpenAiApiKeyPrefsKey, "");
                if (!string.IsNullOrEmpty(stored)) return stored;

                // Optional fallback for headless/editor automation.
                return Environment.GetEnvironmentVariable("HARNESS_VISION_OPENAI_API_KEY")
                    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? Environment.GetEnvironmentVariable("MIMO_API_KEY")
                    ?? "";
            }
        }

        public bool HasOpenAiApiKey => !string.IsNullOrWhiteSpace(OpenAiApiKey);

        public string OpenAiApiKeySource
        {
            get
            {
                if (!string.IsNullOrEmpty(EditorPrefs.GetString(OpenAiApiKeyPrefsKey, "")))
                    return "EditorPrefs";
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARNESS_VISION_OPENAI_API_KEY")))
                    return "HARNESS_VISION_OPENAI_API_KEY";
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
                    return "OPENAI_API_KEY";
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIMO_API_KEY")))
                    return "MIMO_API_KEY";
                return "none";
            }
        }

        public string StoredOpenAiApiKey
        {
            get => EditorPrefs.GetString(OpenAiApiKeyPrefsKey, "");
            set
            {
                if (string.IsNullOrEmpty(value))
                    EditorPrefs.DeleteKey(OpenAiApiKeyPrefsKey);
                else
                    EditorPrefs.SetString(OpenAiApiKeyPrefsKey, value);
            }
        }

        public string BuildOpenAiEndpoint()
        {
            return BuildChatCompletionsEndpoint(OpenAiBaseUrlOrDefault);
        }

        public bool TryValidateOpenAiCompatible(out string error)
        {
            error = null;
            if (ProviderNormalized != "openai-compatible")
            {
                error = "Vision provider is not openai-compatible.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(OpenAiBaseUrlOrDefault))
            {
                error = "OpenAI-compatible base URL is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(OpenAiModelOrDefault))
            {
                error = "OpenAI-compatible model is empty.";
                return false;
            }

            string authScheme = OpenAiAuthSchemeNormalized;
            if (authScheme != "none" && authScheme != "bearer" && authScheme != "raw")
            {
                error = "OpenAI-compatible auth scheme must be one of: none, bearer, raw.";
                return false;
            }

            if (authScheme != "none")
            {
                if (string.IsNullOrWhiteSpace(OpenAiApiKeyHeaderOrDefault))
                {
                    error = "OpenAI-compatible API key header is empty.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(OpenAiApiKey))
                {
                    error = "OpenAI-compatible API key is empty. Configure it in Project Settings > Harness > Vision.";
                    return false;
                }
            }

            return true;
        }

        public void ApplyMiMoPreset()
        {
            provider = "openai-compatible";
            openAiBaseUrl = "https://api.xiaomimimo.com";
            openAiModel = "mimo-v2.5";
            openAiApiKeyHeader = "api-key";
            openAiAuthScheme = "raw";
            openAiTimeoutMs = 60000;
            openAiMaxCompletionTokens = 2048;
            Save();
        }

        public void ApplyOpenAiPreset()
        {
            provider = "openai-compatible";
            openAiBaseUrl = DefaultOpenAiBaseUrl;
            openAiModel = DefaultOpenAiModel;
            openAiApiKeyHeader = "Authorization";
            openAiAuthScheme = "bearer";
            Save();
        }

        public void ApplyLocalPreset()
        {
            provider = "openai-compatible";
            openAiBaseUrl = "http://localhost:1234";
            openAiModel = "local-vlm";
            openAiApiKeyHeader = "Authorization";
            openAiAuthScheme = "none";
            Save();
        }

        public void Save()
        {
            try
            {
                string json = JsonUtility.ToJson(this, true);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Harness.Vision] Failed to save settings: {ex.Message}");
            }
        }

        internal static void ReloadForTests()
        {
            s_instance = Load();
        }

        internal static string BuildChatCompletionsEndpoint(string baseUrl)
        {
            string value = string.IsNullOrWhiteSpace(baseUrl)
                ? DefaultOpenAiBaseUrl
                : baseUrl.Trim().TrimEnd('/');

            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return value + "/chat/completions";
            return value + "/v1/chat/completions";
        }

        private static VisionSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var settings = JsonUtility.FromJson<VisionSettings>(json);
                    return settings ?? new VisionSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Harness.Vision] Failed to load settings: {ex.Message}");
            }

            return new VisionSettings();
        }

        private static string OpenAiApiKeyPrefsKey =>
            "HarnessVision_OpenAiApiKey_" + StablePathHash(Application.dataPath);

        private static string StablePathHash(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/').ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
