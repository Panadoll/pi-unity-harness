using Pi.UnityHarness.Editor.Capabilities.Vision;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    public sealed class VisionJsonTests
    {
        // ─── Capture JSON ────────────────────────────────────────────

        [Test]
        public void BuildCaptureJson_StatusSucceeded_IncludesAllFields()
        {
            string json = VisionJson.BuildCaptureJson(
                "succeeded", "/tmp/shot.png", "game",
                1280, 720, 45678,
                1280, 720,
                826f, 410f,
                1920, 1080,
                1.549f, 1.756f,
                0.6453f, 0.5694f,
                "2026-01-01T12:00:00.000Z");

            Assert.That(json, Does.StartWith("{\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.capture.v1\""));
            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"source\":\"game\""));
            Assert.That(json, Does.Contain("\"width\":1280"));
            Assert.That(json, Does.Contain("\"height\":720"));
            Assert.That(json, Does.Contain("\"bytes\":45678"));
            Assert.That(json, Does.Contain("\"output_size\":{\"w\":1280,\"h\":720}"));
            Assert.That(json, Does.Contain("\"gameview_size\":{\"w\":826,\"h\":410}"));
            Assert.That(json, Does.Contain("\"capture_source_size\":{\"w\":1920,\"h\":1080}"));
            Assert.That(json, Does.Contain("\"captured_at_utc\":\"2026-01-01T12:00:00.000Z\""));
        }

        [Test]
        public void BuildCaptureJson_StatusFailed_NoCaptureFields()
        {
            string json = VisionJson.BuildCaptureJson(
                "failed", null, null, 0, 0, 0,
                null, null, null, null, null, null,
                null, null, null, null, null,
                "Something went wrong", "runtime");

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error\":\"Something went wrong\""));
            Assert.That(json, Does.Contain("\"error_type\":\"runtime\""));
            // Should not contain capture-specific fields
            Assert.That(json, Does.Not.Contain("\"path\""));
            Assert.That(json, Does.Not.Contain("\"width\""));
        }

        [Test]
        public void BuildCaptureJson_SceneSource_NoGameViewFields()
        {
            string json = VisionJson.BuildCaptureJson(
                "succeeded", "/tmp/shot.png", "scene",
                800, 600, 12345,
                800, 600,
                null, null, null, null,
                null, null, null, null,
                "2026-01-01T12:00:00.000Z");

            Assert.That(json, Does.Contain("\"source\":\"scene\""));
            Assert.That(json, Does.Not.Contain("gameview_size"));
            Assert.That(json, Does.Not.Contain("capture_source_size"));
            Assert.That(json, Does.Not.Contain("scale"));
            Assert.That(json, Does.Not.Contain("screenshot_to_gameview"));
        }

        // ─── Analysis Unavailable JSON ───────────────────────────────

        [Test]
        public void BuildAnalysisUnavailableJson_ReturnsCorrectStructure()
        {
            string json = VisionJson.BuildAnalysisUnavailableJson("Find the Start button.");

            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.analysis.v1\""));
            Assert.That(json, Does.Contain("\"status\":\"unavailable\""));
            Assert.That(json, Does.Contain("\"question\":\"Find the Start button.\""));
            Assert.That(json, Does.Contain("\"answer\""));
            Assert.That(json, Does.Contain("no vision analyzer is configured"));
            Assert.That(json, Does.Contain("\"provider\":\"none\""));
            Assert.That(json, Does.Contain("\"visible_text\":[]"));
            Assert.That(json, Does.Contain("\"targets\":[]"));
            Assert.That(json, Does.Contain("\"suggested_actions\":[]"));
        }

        [Test]
        public void BuildAnalysisSucceededJson_IncludesProviderDiagnostics()
        {
            string json = VisionJson.BuildAnalysisSucceededJson(
                "Q.",
                "Answer.",
                "openai-compatible",
                "mimo-v2.5",
                "https://api.xiaomimimo.com/v1/chat/completions",
                1234);

            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"answer\":\"Answer.\""));
            Assert.That(json, Does.Contain("\"provider\":\"openai-compatible\""));
            Assert.That(json, Does.Contain("\"model\":\"mimo-v2.5\""));
            Assert.That(json, Does.Contain("\"latency_ms\":1234"));
        }

        [Test]
        public void BuildAnalysisSucceededJson_PreservesStructuredArrays()
        {
            string json = VisionJson.BuildAnalysisSucceededJson(
                "Q.",
                "Answer.",
                "[{\"text\":\"Start\"}]",
                "[{\"label\":\"Start\",\"coordinate_space\":\"screenshot_top_left\"}]",
                "[{\"type\":\"click\",\"target_label\":\"Start\"}]",
                "openai-compatible",
                "mimo-v2.5",
                "https://api.xiaomimimo.com/v1/chat/completions",
                1234);

            Assert.That(json, Does.Contain("\"visible_text\":[{\"text\":\"Start\"}]"));
            Assert.That(json, Does.Contain("\"targets\":[{\"label\":\"Start\""));
            Assert.That(json, Does.Contain("\"suggested_actions\":[{\"type\":\"click\""));
        }

        [Test]
        public void BuildProviderTestJson_DoesNotExposeApiKey()
        {
            string json = VisionJson.BuildProviderTestJson(
                "configured", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions",
                true, "EditorPrefs", 12);

            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.provider_test.v1\""));
            Assert.That(json, Does.Contain("\"has_api_key\":true"));
            Assert.That(json, Does.Contain("\"api_key_source\":\"EditorPrefs\""));
            Assert.That(json, Does.Contain("\"warnings\":[]"));
            Assert.That(json, Does.Not.Contain("sk-"));
        }

        [Test]
        public void BuildProviderTestJson_CanReturnWarningWithoutFailure()
        {
            string json = VisionJson.BuildProviderTestJson(
                "succeeded", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions",
                true, "EditorPrefs", 12, null, null, "empty assistant content");

            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"warnings\":[\"empty assistant content\"]"));
            Assert.That(json, Does.Not.Contain("\"error\""));
        }

        // ─── Analysis Skipped JSON ───────────────────────────────────

        [Test]
        public void BuildAnalysisSkippedJson_ReturnsCorrectStructure()
        {
            string json = VisionJson.BuildAnalysisSkippedJson();

            Assert.That(json, Does.Contain("\"status\":\"skipped\""));
            Assert.That(json, Does.Contain("capture failed"));
            Assert.That(json, Does.Contain("\"visible_text\":[]"));
            Assert.That(json, Does.Contain("\"targets\":[]"));
            Assert.That(json, Does.Contain("\"suggested_actions\":[]"));
        }

        // ─── Compound Response JSON ──────────────────────────────────

        [Test]
        public void BuildCompoundCaptureAnalysisJson_IncludesBothParts()
        {
            string captureJson = "{\"status\":\"succeeded\",\"path\":\"/tmp/shot.png\"}";
            string analysisJson = "{\"status\":\"unavailable\",\"answer\":\"No analyzer.\"}";
            string json = VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, "partial");

            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.capture_analysis.v1\""));
            Assert.That(json, Does.Contain("\"status\":\"partial\""));
            Assert.That(json, Does.Contain("\"capture\":{"));
            Assert.That(json, Does.Contain("\"analysis\":{"));
            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"answer\":\"No analyzer.\""));
        }

        [Test]
        public void BuildCompoundCaptureAnalysisJson_FailedStatus()
        {
            string captureJson = "{\"status\":\"failed\",\"error\":\"No camera\"}";
            string analysisJson = "{\"status\":\"skipped\"}";
            string json = VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, "failed");

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
        }

        // ─── Analysis Request JSON ───────────────────────────────────

        [Test]
        public void BuildAnalysisRequestJson_IncludesRequiredFields()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"source\":\"game\",\"width\":1280,\"height\":720}";
            string json = VisionJson.BuildAnalysisRequestJson("Find the button.", captureJson, null);

            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.analysis_request.v1\""));
            Assert.That(json, Does.Contain("\"question\":\"Find the button.\""));
            Assert.That(json, Does.Contain("\"image\":{"));
            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"context\":{"));
            Assert.That(json, Does.Contain("\"output\":{"));
            Assert.That(json, Does.Contain("\"constraints\":{"));
        }

        [Test]
        public void BuildAnalysisRequestJson_PreservesCaptureFields()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"source\":\"game\",\"width\":1280,\"height\":720,\"bytes\":45678,\"output_size\":{\"w\":1280,\"h\":720}}";
            string json = VisionJson.BuildAnalysisRequestJson("Describe.", captureJson, null);

            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"source\":\"game\""));
            Assert.That(json, Does.Contain("\"width\":1280"));
            Assert.That(json, Does.Contain("\"height\":720"));
            Assert.That(json, Does.Contain("\"bytes\":45678"));
            Assert.That(json, Does.Contain("\"output_size\":{\"w\":1280,\"h\":720}"));
        }

        [Test]
        public void BuildAnalysisRequestJson_EmptyCapture_DoesNotCrash()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Question.", "{}", null);
            Assert.That(json, Does.Contain("\"question\":\"Question.\""));
        }

        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenNull()
        {
            string json = HarnessVision.BuildAnalysisRequestJson(null, "{\"path\":\"/tmp/shot.png\"}", null);

            Assert.That(json, Does.Contain("\"question\""));
            Assert.That(json, Does.Not.Contain("null"));
        }

        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenEmpty()
        {
            string json = HarnessVision.BuildAnalysisRequestJson("", "{\"path\":\"/tmp/shot.png\"}", null);

            Assert.That(json, Does.Contain("Describe what is visible"));
        }

        [Test]
        public void BuildAnalysisRequestJson_WithContext_IncludesContext()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string contextJson = "{\"task\":\"Click Start\",\"uitree\":null}";
            string json = VisionJson.BuildAnalysisRequestJson("Question.", captureJson, contextJson);

            Assert.That(json, Does.Contain("\"task\":\"Click Start\""));
        }

        [Test]
        public void BuildAnalysisRequestJson_IncludesOutputPreferences()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{}", null);
            Assert.That(json, Does.Contain("\"coordinate_space\":\"screenshot_top_left\""));
            Assert.That(json, Does.Contain("\"max_targets\":10"));
            Assert.That(json, Does.Contain("\"do_not_guess\":true"));
        }

        [Test]
        public void BuildAnalysisRequestJson_FormattedCaptureJson_PreservesFields()
        {
            string captureJson = "{ \n  \"path\" : \"/tmp/shot.png\", \n  \"source\" : \"game\", \n  \"width\" : 1280, \n  \"height\" : 720, \n  \"screenshot_to_gameview\" : { \"x\" : 0.5, \"y\" : 0.5 } \n}";
            string json = VisionJson.BuildAnalysisRequestJson("Q.", captureJson, null);

            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"source\":\"game\""));
            Assert.That(json, Does.Contain("\"width\":1280"));
            Assert.That(json, Does.Contain("\"screenshot_to_gameview\":{ \"x\" : 0.5, \"y\" : 0.5 }"));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidContext_FallsBackToEmptyObject()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{\"path\":\"/tmp/shot.png\"}", "not json");

            Assert.That(json, Does.Contain("\"context\":{}"));
            Assert.That(json, Does.Not.Contain("not json"));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidCaptureJson_DropsImageFields()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{\"path\":oops}", null);

            Assert.That(json, Does.Contain("\"image\":{}"));
            Assert.That(json, Does.Not.Contain("oops"));
        }

        [Test]
        public void BuildCaptureJsonFromMetadata_PreservesCoordinateMapping()
        {
            string captureJson = "{\"status\":\"succeeded\",\"path\":\"old.png\",\"source\":\"game\",\"width\":1280,\"height\":720,\"bytes\":1,\"output_size\":{\"w\":1280,\"h\":720},\"gameview_size\":{\"w\":826,\"h\":410},\"capture_source_size\":{\"w\":1920,\"h\":1080},\"scale\":{\"x\":1.5,\"y\":1.7},\"screenshot_to_gameview\":{\"x\":0.64,\"y\":0.57},\"captured_at_utc\":\"2026-01-01T12:00:00.000Z\"}";
            string json = VisionJson.BuildCaptureJsonFromMetadata("new.png", captureJson, 0, 0, 123, "2026-01-02T12:00:00.000Z");

            Assert.That(json, Does.Contain("\"path\":\"new.png\""));
            Assert.That(json, Does.Contain("\"width\":1280"));
            Assert.That(json, Does.Contain("\"height\":720"));
            Assert.That(json, Does.Contain("\"bytes\":123"));
            Assert.That(json, Does.Contain("\"gameview_size\":{\"w\":826,\"h\":410}"));
            Assert.That(json, Does.Contain("\"capture_source_size\":{\"w\":1920,\"h\":1080}"));
            Assert.That(json, Does.Contain("\"scale\":{\"x\":1.5,\"y\":1.7}"));
            Assert.That(json, Does.Contain("\"screenshot_to_gameview\":{\"x\":0.64,\"y\":0.57}"));
            Assert.That(json, Does.Contain("\"captured_at_utc\":\"2026-01-01T12:00:00.000Z\""));
        }

        // ─── JSON Escaping ───────────────────────────────────────────

        [Test]
        public void CaptureJson_EscapesSpecialCharacters()
        {
            string json = VisionJson.BuildCaptureJson(
                "succeeded", "C:\\path\\with\bspec\tial\r\nchars", "game",
                100, 100, 0,
                100, 100,
                null, null, null, null,
                null, null, null, null,
                null);

            // Path should be escaped
            Assert.That(json, Does.Contain("C:\\\\path\\\\with"));
            // Backspace, tab, carriage return, newline should be escaped
            Assert.That(json, Does.Not.Contain("\b")); // no raw control chars
        }
    }
}
