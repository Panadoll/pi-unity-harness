using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    public sealed class VisionJsonTests
    {
        private static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                return JObject.Load(reader);
            }
        }

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

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("succeeded"));
            Assert.That((string)parsed["schema"], Is.EqualTo("harness.vision.capture.v1"));
            Assert.That((string)parsed["path"], Is.EqualTo("/tmp/shot.png"));
            Assert.That((bool)parsed["embed"], Is.False);
            Assert.That((string)parsed["source"], Is.EqualTo("game"));
            Assert.That((int)parsed["width"], Is.EqualTo(1280));
            Assert.That((int)parsed["height"], Is.EqualTo(720));
            Assert.That((long)parsed["bytes"], Is.EqualTo(45678));
            Assert.That((int)parsed["output_size"]["w"], Is.EqualTo(1280));
            Assert.That((int)parsed["output_size"]["h"], Is.EqualTo(720));
            Assert.That((int)parsed["gameview_size"]["w"], Is.EqualTo(826));
            Assert.That((int)parsed["gameview_size"]["h"], Is.EqualTo(410));
            Assert.That((int)parsed["capture_source_size"]["w"], Is.EqualTo(1920));
            Assert.That((int)parsed["capture_source_size"]["h"], Is.EqualTo(1080));
            Assert.That((string)parsed["captured_at_utc"], Is.EqualTo("2026-01-01T12:00:00.000Z"));
        }

        [Test]
        public void BuildCaptureJson_StatusFailed_NoCaptureFields()
        {
            string json = VisionJson.BuildCaptureJson(
                "failed", null, null, 0, 0, 0,
                null, null, null, null, null, null,
                null, null, null, null, null,
                "Something went wrong", "runtime");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("failed"));
            Assert.That((string)parsed["error"], Is.EqualTo("Something went wrong"));
            Assert.That((string)parsed["error_type"], Is.EqualTo("runtime"));
            Assert.That(parsed["path"], Is.Null);
            Assert.That(parsed["width"], Is.Null);
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

            JObject parsed = Parse(json);
            Assert.That((string)parsed["source"], Is.EqualTo("scene"));
            Assert.That(parsed["gameview_size"], Is.Null);
            Assert.That(parsed["capture_source_size"], Is.Null);
            Assert.That(parsed["scale"], Is.Null);
            Assert.That(parsed["screenshot_to_gameview"], Is.Null);
        }

        // ─── Analysis Unavailable JSON ───────────────────────────────

        [Test]
        public void BuildAnalysisUnavailableJson_ReturnsCorrectStructure()
        {
            string json = VisionJson.BuildAnalysisUnavailableJson("Find the Start button.");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["schema"], Is.EqualTo("harness.vision.analysis.v1"));
            Assert.That((string)parsed["status"], Is.EqualTo("unavailable"));
            Assert.That((string)parsed["question"], Is.EqualTo("Find the Start button."));
            Assert.That((string)parsed["answer"], Is.Not.Null.And.Not.Empty);
            Assert.That((string)parsed["diagnostics"]["provider"], Is.EqualTo("none"));
            Assert.That(parsed["visible_text"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(parsed["visible_text"].HasValues, Is.False);
            Assert.That(parsed["targets"].HasValues, Is.False);
            Assert.That(parsed["suggested_actions"].HasValues, Is.False);
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

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("succeeded"));
            Assert.That((string)parsed["answer"], Is.EqualTo("Answer."));
            Assert.That((string)parsed["diagnostics"]["provider"], Is.EqualTo("openai-compatible"));
            Assert.That((string)parsed["diagnostics"]["model"], Is.EqualTo("mimo-v2.5"));
            Assert.That((int)parsed["diagnostics"]["latency_ms"], Is.EqualTo(1234));
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

            JObject parsed = Parse(json);
            Assert.That((string)parsed["visible_text"][0]["text"], Is.EqualTo("Start"));
            Assert.That((string)parsed["targets"][0]["label"], Is.EqualTo("Start"));
            Assert.That((string)parsed["suggested_actions"][0]["type"], Is.EqualTo("click"));
        }

        [Test]
        public void BuildProviderTestJson_DoesNotExposeApiKey()
        {
            string json = VisionJson.BuildProviderTestJson(
                "configured", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions",
                true, "EditorPrefs", 12);

            JObject parsed = Parse(json);
            Assert.That((string)parsed["schema"], Is.EqualTo("harness.vision.provider_test.v1"));
            Assert.That((bool)parsed["diagnostics"]["has_api_key"], Is.True);
            Assert.That((string)parsed["diagnostics"]["api_key_source"], Is.EqualTo("EditorPrefs"));
            Assert.That(parsed["warnings"].HasValues, Is.False);
            Assert.That(parsed["diagnostics"]["api_key"], Is.Null);
        }

        [Test]
        public void BuildProviderTestJson_CanReturnWarningWithoutFailure()
        {
            string json = VisionJson.BuildProviderTestJson(
                "succeeded", "openai-compatible", "mimo-v2.5", "https://example/v1/chat/completions",
                true, "EditorPrefs", 12, null, null, "empty assistant content");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("succeeded"));
            Assert.That((string)parsed["warnings"][0], Is.EqualTo("empty assistant content"));
            Assert.That(parsed["error"], Is.Null);
        }

        // ─── Analysis Skipped JSON ───────────────────────────────────

        [Test]
        public void BuildAnalysisSkippedJson_ReturnsCorrectStructure()
        {
            string json = VisionJson.BuildAnalysisSkippedJson();

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("skipped"));
            Assert.That((string)parsed["answer"], Is.Not.Null.And.Not.Empty);
            Assert.That(parsed["visible_text"].HasValues, Is.False);
            Assert.That(parsed["targets"].HasValues, Is.False);
            Assert.That(parsed["suggested_actions"].HasValues, Is.False);
        }

        // ─── Compound Response JSON ──────────────────────────────────

        [Test]
        public void BuildCompoundCaptureAnalysisJson_IncludesBothParts()
        {
            string captureJson = "{\"status\":\"succeeded\",\"path\":\"/tmp/shot.png\"}";
            string analysisJson = "{\"status\":\"unavailable\",\"answer\":\"No analyzer.\"}";
            string json = VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, "partial");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["schema"], Is.EqualTo("harness.vision.capture_analysis.v1"));
            Assert.That((string)parsed["status"], Is.EqualTo("partial"));
            Assert.That((string)parsed["capture"]["path"], Is.EqualTo("/tmp/shot.png"));
            Assert.That((string)parsed["analysis"]["answer"], Is.EqualTo("No analyzer."));
        }

        [Test]
        public void BuildCompoundCaptureAnalysisJson_FailedStatus()
        {
            string captureJson = "{\"status\":\"failed\",\"error\":\"No camera\"}";
            string analysisJson = "{\"status\":\"skipped\"}";
            string json = VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, "failed");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["status"], Is.EqualTo("failed"));
            Assert.That((string)parsed["capture"]["error"], Is.EqualTo("No camera"));
        }

        // ─── Analysis Request JSON ───────────────────────────────────

        [Test]
        public void BuildAnalysisRequestJson_IncludesRequiredFields()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"source\":\"game\",\"width\":1280,\"height\":720}";
            string json = VisionJson.BuildAnalysisRequestJson("Find the button.", captureJson, null);

            JObject parsed = Parse(json);
            Assert.That((string)parsed["schema"], Is.EqualTo("harness.vision.analysis_request.v1"));
            Assert.That((string)parsed["question"], Is.EqualTo("Find the button."));
            Assert.That((string)parsed["image"]["path"], Is.EqualTo("/tmp/shot.png"));
            Assert.That(parsed["context"].Type, Is.EqualTo(JTokenType.Object));
            Assert.That(parsed["output"].Type, Is.EqualTo(JTokenType.Object));
            Assert.That(parsed["constraints"].Type, Is.EqualTo(JTokenType.Object));
        }

        [Test]
        public void BuildAnalysisRequestJson_PreservesCaptureFields()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"embed\":false,\"source\":\"game\",\"width\":1280,\"height\":720,\"bytes\":45678,\"output_size\":{\"w\":1280,\"h\":720}}";
            string json = VisionJson.BuildAnalysisRequestJson("Describe.", captureJson, null);

            JObject image = Parse(json)["image"] as JObject;
            Assert.That((string)image["path"], Is.EqualTo("/tmp/shot.png"));
            Assert.That((bool)image["embed"], Is.False);
            Assert.That((string)image["source"], Is.EqualTo("game"));
            Assert.That((int)image["width"], Is.EqualTo(1280));
            Assert.That((int)image["height"], Is.EqualTo(720));
            Assert.That((int)image["bytes"], Is.EqualTo(45678));
            Assert.That((int)image["output_size"]["w"], Is.EqualTo(1280));
            Assert.That((int)image["output_size"]["h"], Is.EqualTo(720));
        }

        [Test]
        public void BuildAnalysisRequestJson_EmptyCapture_OmitsImageAndContextValues()
        {
            JObject parsed = Parse(VisionJson.BuildAnalysisRequestJson("Question.", "{}", null));
            Assert.That((string)parsed["question"], Is.EqualTo("Question."));
            Assert.That(parsed["image"].HasValues, Is.False);
            Assert.That(parsed["context"].HasValues, Is.False);
        }

        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenNull()
        {
            string json = HarnessVision.BuildAnalysisRequestJson(null, "{\"path\":\"/tmp/shot.png\"}", null);

            JObject parsed = Parse(json);
            Assert.That((string)parsed["question"], Is.Not.Null.And.Not.Empty);
            Assert.That(parsed["question"].Type, Is.EqualTo(JTokenType.String));
        }

        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenEmpty()
        {
            string json = HarnessVision.BuildAnalysisRequestJson("", "{\"path\":\"/tmp/shot.png\"}", null);

            Assert.That((string)Parse(json)["question"], Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void BuildAnalysisRequestJson_WithContext_IncludesContext()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string contextJson = "{\"task\":\"Click Start\",\"uitree\":null}";
            string json = VisionJson.BuildAnalysisRequestJson("Question.", captureJson, contextJson);

            Assert.That((string)Parse(json)["context"]["task"], Is.EqualTo("Click Start"));
        }

        [Test]
        public void BuildAnalysisRequestJson_IncludesOutputPreferences()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{}", null);
            JObject parsed = Parse(json);
            Assert.That((string)parsed["output"]["coordinate_space"], Is.EqualTo("screenshot_top_left"));
            Assert.That((int)parsed["output"]["max_targets"], Is.EqualTo(10));
            Assert.That((bool)parsed["constraints"]["do_not_guess"], Is.True);
        }

        [Test]
        public void BuildAnalysisRequestJson_FormattedCaptureJson_PreservesFields()
        {
            string captureJson = "{ \n  \"path\" : \"/tmp/shot.png\", \n  \"source\" : \"game\", \n  \"width\" : 1280, \n  \"height\" : 720, \n  \"screenshot_to_gameview\" : { \"x\" : 0.5, \"y\" : 0.5 } \n}";
            string json = VisionJson.BuildAnalysisRequestJson("Q.", captureJson, null);

            JObject image = Parse(json)["image"] as JObject;
            Assert.That((string)image["path"], Is.EqualTo("/tmp/shot.png"));
            Assert.That((string)image["source"], Is.EqualTo("game"));
            Assert.That((int)image["width"], Is.EqualTo(1280));
            Assert.That((float)image["screenshot_to_gameview"]["x"], Is.EqualTo(0.5f));
            Assert.That((float)image["screenshot_to_gameview"]["y"], Is.EqualTo(0.5f));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidContext_FallsBackToEmptyObject()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{\"path\":\"/tmp/shot.png\"}", "not json");

            JObject parsed = Parse(json);
            Assert.That(parsed["context"].HasValues, Is.False);
            Assert.That(parsed.ToString(), Does.Not.Contain("not json"));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidCaptureJson_DropsImageFields()
        {
            string json = VisionJson.BuildAnalysisRequestJson("Q.", "{\"path\":oops}", null);

            JObject parsed = Parse(json);
            Assert.That(parsed["image"].HasValues, Is.False);
            Assert.That(parsed.ToString(), Does.Not.Contain("oops"));
        }

        [Test]
        public void BuildCaptureJsonFromMetadata_PreservesCoordinateMapping()
        {
            string captureJson = "{\"status\":\"succeeded\",\"path\":\"old.png\",\"embed\":false,\"source\":\"game\",\"width\":1280,\"height\":720,\"bytes\":1,\"output_size\":{\"w\":1280,\"h\":720},\"gameview_size\":{\"w\":826,\"h\":410},\"capture_source_size\":{\"w\":1920,\"h\":1080},\"scale\":{\"x\":1.5,\"y\":1.7},\"screenshot_to_gameview\":{\"x\":0.64,\"y\":0.57},\"captured_at_utc\":\"2026-01-01T12:00:00.000Z\"}";
            string json = VisionJson.BuildCaptureJsonFromMetadata("new.png", captureJson, 0, 0, 123, "2026-01-02T12:00:00.000Z");

            JObject parsed = Parse(json);
            Assert.That((string)parsed["path"], Is.EqualTo("new.png"));
            Assert.That((bool)parsed["embed"], Is.False);
            Assert.That((int)parsed["width"], Is.EqualTo(1280));
            Assert.That((int)parsed["height"], Is.EqualTo(720));
            Assert.That((int)parsed["bytes"], Is.EqualTo(123));
            Assert.That((int)parsed["gameview_size"]["w"], Is.EqualTo(826));
            Assert.That((int)parsed["gameview_size"]["h"], Is.EqualTo(410));
            Assert.That((int)parsed["capture_source_size"]["w"], Is.EqualTo(1920));
            Assert.That((int)parsed["capture_source_size"]["h"], Is.EqualTo(1080));
            Assert.That((float)parsed["scale"]["x"], Is.EqualTo(1.5f));
            Assert.That((float)parsed["scale"]["y"], Is.EqualTo(1.7f));
            Assert.That((float)parsed["screenshot_to_gameview"]["x"], Is.EqualTo(0.64f));
            Assert.That((float)parsed["screenshot_to_gameview"]["y"], Is.EqualTo(0.57f));
            Assert.That((string)parsed["captured_at_utc"], Is.EqualTo("2026-01-01T12:00:00.000Z"));
        }

        [Test]
        public void CaptureJson_RoundTripsControlCharactersInPath()
        {
            var path = new System.Text.StringBuilder("C:\\path\\\"雪");
            for (int c = 0; c < 32; c++)
                path.Append((char)c);
            string raw = path.ToString();
            string json = VisionJson.BuildCaptureJson(
                "succeeded", raw, "game",
                100, 100, 0,
                100, 100,
                null, null, null, null,
                null, null, null, null,
                null);

            JObject parsed = Parse(json);
            Assert.That((string)parsed["path"], Is.EqualTo(raw));
            for (int c = 0; c < 32; c++)
                Assert.That(json.IndexOf((char)c), Is.LessThan(0));
        }
    }
}
