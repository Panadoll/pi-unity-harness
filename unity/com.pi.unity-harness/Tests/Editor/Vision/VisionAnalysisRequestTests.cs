using Pi.UnityHarness.Editor.Capabilities.Vision;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    /// <summary>
    /// Tests for BuildAnalysisRequestJson behavior:
    /// - Input validation
    /// - Field preservation from capture JSON
    /// - Context passthrough
    /// - Schema structure
    /// </summary>
    public sealed class VisionAnalysisRequestTests
    {
        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenNull()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"width\":1280,\"height\":720}";
            string json = HarnessVision.BuildAnalysisRequestJson(null, captureJson, null);

            Assert.That(json, Does.Contain("\"question\""));
            // Should use a default question, not null
            Assert.That(json, Does.Not.Contain("null"));
        }

        [Test]
        public void BuildAnalysisRequestJson_DefaultQuestionWhenEmpty()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string json = HarnessVision.BuildAnalysisRequestJson("", captureJson, null);

            Assert.That(json, Does.Contain("Describe what is visible"));
        }

        [Test]
        public void BuildAnalysisRequestJson_EmptyCaptureJson_DoesNotCrash()
        {
            string json = HarnessVision.BuildAnalysisRequestJson("Find the button.", null, null);
            Assert.That(json, Does.Contain("\"question\":\"Find the button.\""));
            // Should handle null capture gracefully
            Assert.That(json, Does.Contain("\"image\":{"));
        }

        [Test]
        public void BuildAnalysisRequestJson_PreservesScaleAndMappingFields()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"source\":\"game\",\"width\":1280,\"height\":720," +
                "\"scale\":{\"x\":1.549,\"y\":1.756}," +
                "\"screenshot_to_gameview\":{\"x\":0.6453,\"y\":0.5694}}";
            string json = HarnessVision.BuildAnalysisRequestJson("Describe the scene.", captureJson, null);

            Assert.That(json, Does.Contain("\"scale\":{\"x\":1.549,\"y\":1.756}"));
            Assert.That(json, Does.Contain("\"screenshot_to_gameview\":{\"x\":0.6453,\"y\":0.5694}"));
        }

        [Test]
        public void BuildAnalysisRequestJson_SchemaFieldPresent()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string json = HarnessVision.BuildAnalysisRequestJson("Q.", captureJson, null);

            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.analysis_request.v1\""));
        }

        [Test]
        public void BuildAnalysisRequestJson_OutputSectionPresent()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string json = HarnessVision.BuildAnalysisRequestJson("Q.", captureJson, null);

            Assert.That(json, Does.Contain("\"output\":{"));
            Assert.That(json, Does.Contain("\"coordinate_space\":\"screenshot_top_left\""));
            Assert.That(json, Does.Contain("\"language\":\"zh-CN\""));
        }

        [Test]
        public void BuildAnalysisRequestJson_ConstraintsSectionPresent()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string json = HarnessVision.BuildAnalysisRequestJson("Q.", captureJson, null);

            Assert.That(json, Does.Contain("\"constraints\":{"));
            Assert.That(json, Does.Contain("\"do_not_guess\":true"));
            Assert.That(json, Does.Contain("\"min_confidence_for_action\":0.7"));
        }

        [Test]
        public void BuildAnalysisRequestJson_WithContextJson_IncludesIt()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\"}";
            string contextJson = "{\"task\":\"Click the Start button on the main menu.\",\"status\":null}";
            string json = HarnessVision.BuildAnalysisRequestJson("Find Start button.", captureJson, contextJson);

            Assert.That(json, Does.Contain("\"task\":\"Click the Start button on the main menu.\""));
        }

        [Test]
        public void BuildAnalysisRequestJson_OutputIsValidJson()
        {
            string captureJson = "{\"path\":\"/tmp/shot.png\",\"source\":\"game\",\"width\":1280,\"height\":720," +
                "\"scale\":{\"x\":1.5,\"y\":1.5}}";
            string json = HarnessVision.BuildAnalysisRequestJson("What is visible?", captureJson, "{\"task\":\"test\"}");

            int openBraces = json.Split('{').Length - 1;
            int closeBraces = json.Split('}').Length - 1;
            Assert.That(openBraces, Is.EqualTo(closeBraces));
        }

        [Test]
        public void BuildAnalysisRequestJson_FormattedCaptureJson_PreservesFields()
        {
            string captureJson = "{ \"path\" : \"/tmp/shot.png\", \"source\" : \"game\", \"width\" : 1280, \"height\" : 720, \"screenshot_to_gameview\" : { \"x\" : 0.5, \"y\" : 0.5 } }";
            string json = HarnessVision.BuildAnalysisRequestJson("What is visible?", captureJson, null);

            Assert.That(json, Does.Contain("\"path\":\"/tmp/shot.png\""));
            Assert.That(json, Does.Contain("\"source\":\"game\""));
            Assert.That(json, Does.Contain("\"width\":1280"));
            Assert.That(json, Does.Contain("\"screenshot_to_gameview\":{ \"x\" : 0.5, \"y\" : 0.5 }"));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidContext_FallsBackToEmptyObject()
        {
            string json = HarnessVision.BuildAnalysisRequestJson("What is visible?", "{\"path\":\"/tmp/shot.png\"}", "not json");

            Assert.That(json, Does.Contain("\"context\":{}"));
            Assert.That(json, Does.Not.Contain("not json"));
        }

        [Test]
        public void BuildAnalysisRequestJson_InvalidCaptureJson_DropsImageFields()
        {
            string json = HarnessVision.BuildAnalysisRequestJson("What is visible?", "{\"path\":oops}", null);

            Assert.That(json, Does.Contain("\"image\":{}"));
            Assert.That(json, Does.Not.Contain("oops"));
        }
    }
}
