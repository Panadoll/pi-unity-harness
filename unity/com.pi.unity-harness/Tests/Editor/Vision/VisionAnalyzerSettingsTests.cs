using Pi.UnityHarness.Editor.Capabilities.Vision;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    public sealed class VisionAnalyzerSettingsTests
    {
        [Test]
        public void BuildChatCompletionsEndpoint_AppendsV1ChatCompletions()
        {
            Assert.That(
                VisionSettings.BuildChatCompletionsEndpoint("https://api.xiaomimimo.com"),
                Is.EqualTo("https://api.xiaomimimo.com/v1/chat/completions"));
            Assert.That(
                VisionSettings.BuildChatCompletionsEndpoint("https://api.openai.com/v1"),
                Is.EqualTo("https://api.openai.com/v1/chat/completions"));
            Assert.That(
                VisionSettings.BuildChatCompletionsEndpoint("http://localhost:1234/v1/chat/completions"),
                Is.EqualTo("http://localhost:1234/v1/chat/completions"));
        }

        [Test]
        public void BuildRequestBody_UsesDataUrlAndOpenAiCompatibleMessageShape()
        {
            byte[] pngBytes = { 1, 2, 3, 4 };
            string body = OpenAiCompatibleVisionAnalyzer.BuildRequestBody(
                "mimo-v2.5",
                "Describe image.",
                "{\"path\":\"shot.png\"}",
                null,
                pngBytes,
                1024);

            Assert.That(body, Does.Contain("\"model\":\"mimo-v2.5\""));
            Assert.That(body, Does.Contain("\"messages\":["));
            Assert.That(body, Does.Contain("\"type\":\"image_url\""));
            Assert.That(body, Does.Contain("data:image/png;base64,AQIDBA=="));
            Assert.That(body, Does.Contain("\"max_completion_tokens\":1024"));
            Assert.That(body, Does.Contain("visible_text"));
            Assert.That(body, Does.Contain("suggested_actions"));
            Assert.That(body, Does.Contain("screenshot_top_left"));
            Assert.That(body, Does.Contain("zh-CN"));
        }

        [Test]
        public void ExtractAssistantText_ReadsOpenAiCompatibleResponse()
        {
            string response = "{\"choices\":[{\"message\":{\"content\":\"A Start button is visible.\"}}]}";

            Assert.That(
                OpenAiCompatibleVisionAnalyzer.ExtractAssistantText(response),
                Is.EqualTo("A Start button is visible."));
        }

        [Test]
        public void BuildAnalysisJsonFromProviderResponse_ParsesStructuredContent()
        {
            string content = "{\"answer\":\"看到开始按钮。\",\"visible_text\":[{\"text\":\"Start\",\"bbox\":{\"x\":1,\"y\":2,\"w\":3,\"h\":4}}],\"targets\":[{\"label\":\"Start\",\"confidence\":0.92,\"coordinate_space\":\"screenshot_top_left\",\"center\":{\"x\":10,\"y\":20},\"bbox\":{\"x\":1,\"y\":2,\"w\":3,\"h\":4}}],\"suggested_actions\":[{\"type\":\"click\",\"target_label\":\"Start\",\"confidence\":0.92}]}";
            string response = "{\"choices\":[{\"message\":{\"content\":\"" + EscapeForJsonString(content) + "\"}}]}";

            string json = OpenAiCompatibleVisionAnalyzer.BuildAnalysisJsonFromProviderResponse(
                "Q.", response, "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions", 123);

            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"answer\":\"看到开始按钮。\""));
            Assert.That(json, Does.Contain("\"visible_text\":[{"));
            Assert.That(json, Does.Contain("\"targets\":[{"));
            Assert.That(json, Does.Contain("\"suggested_actions\":[{"));
        }

        [Test]
        public void BuildAnalysisJsonFromProviderResponse_EmptyBodyIsFailure()
        {
            string json = OpenAiCompatibleVisionAnalyzer.BuildAnalysisJsonFromProviderResponse(
                "Q.", "", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions", 123);

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"type\":\"provider_response_empty\""));
        }

        [Test]
        public void BuildAnalysisJsonFromProviderResponse_MissingContentIsFailure()
        {
            string json = OpenAiCompatibleVisionAnalyzer.BuildAnalysisJsonFromProviderResponse(
                "Q.", "{\"choices\":[{}]}", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions", 123);

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"type\":\"provider_response_parse\""));
        }

        private static string EscapeForJsonString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
