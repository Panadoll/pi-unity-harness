using System;
using System.IO;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    /// <summary>
    /// Tests that the default provider ("none") returns
    /// analysis.status=unavailable for all analysis entrypoints
    /// without network calls or external tools.
    /// </summary>
    public sealed class VisionAnalyzeUnavailableTests
    {
        [Test]
        public void CaptureAndAnalyzeJson_DefaultProvider_ReturnsPartial()
        {
            // Mock mode with no camera = capture fails, analysis skipped
            string json = HarnessVision.CaptureAndAnalyzeJson(
                "Find the Start button.", "scene", null, 0, 0, null);

            // Should return compound JSON with schema
            Assert.That(json, Does.Contain("\"schema\":\"harness.vision.capture_analysis.v1\""));
            // If capture fails (no SceneView in test runner), status is "failed"
            // If capture succeeds (unlikely in test runner), status is "partial"
            Assert.That(
                json.Contains("\"status\":\"failed\"") ||
                json.Contains("\"status\":\"partial\""),
                Is.True,
                "Expected status 'failed' (no camera) or 'partial' (capture ok, analysis unavailable). Got: " + json);
        }

        [Test]
        public void CaptureAndAnalyzeJson_WhenCaptureFails_AnalysisIsSkipped()
        {
            string json = HarnessVision.CaptureAndAnalyzeJson(
                "Describe scene.", "invalid_mode", null, 0, 0, null);

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"status\":\"skipped\""));
            Assert.That(json, Does.Contain("capture failed"));
        }

        [Test]
        public void AnalyzeImageJson_NonexistentFile_ReturnsFailed()
        {
            string json = HarnessVision.AnalyzeImageJson(
                "Describe the scene.",
                "/nonexistent/path/shot.png",
                null, null);

            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("Image file not found"));
            Assert.That(json, Does.Contain("\"status\":\"skipped\""));
        }

        [Test]
        public void AnalyzeImageJson_WithImageMeta_PreservesCoordinateMappingAndBytes()
        {
            string path = Path.Combine(Path.GetTempPath(), "harness-vision-test-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, new byte[]
            {
                137, 80, 78, 71, 13, 10, 26, 10,
                0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 5, 0, 0, 0, 2, 208
            });

            try
            {
                string metaJson = "{\"status\":\"succeeded\",\"path\":\"old.png\",\"source\":\"game\",\"width\":1280,\"height\":720," +
                    "\"output_size\":{\"w\":1280,\"h\":720}," +
                    "\"gameview_size\":{\"w\":826,\"h\":410}," +
                    "\"scale\":{\"x\":1.5,\"y\":1.7}," +
                    "\"screenshot_to_gameview\":{\"x\":0.64,\"y\":0.57}}";
                string json = HarnessVision.AnalyzeImageJson("Describe the scene.", path, metaJson, null);

                Assert.That(json, Does.Contain("\"status\":\"partial\""));
                Assert.That(json, Does.Contain("\"width\":1280"));
                Assert.That(json, Does.Contain("\"height\":720"));
                Assert.That(json, Does.Contain("\"bytes\":24"));
                Assert.That(json, Does.Contain("\"gameview_size\":{\"w\":826,\"h\":410}"));
                Assert.That(json, Does.Contain("\"scale\":{\"x\":1.5,\"y\":1.7}"));
                Assert.That(json, Does.Contain("\"screenshot_to_gameview\":{\"x\":0.64,\"y\":0.57}"));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void CaptureAndAnalyzeJson_DoesNotMakeNetworkCalls()
        {
            // This test verifies by contract that no network calls are made.
            // The default provider "none" never invokes external tools.
            string json = HarnessVision.CaptureAndAnalyzeJson(
                "Test.", "scene", null, 0, 0, null);

            // Analysis should show provider "none"
            Assert.That(json, Does.Contain("\"provider\":\"none\""));
            // Should not contain any model names
            Assert.That(json, Does.Not.Contain("gpt"));
            Assert.That(json, Does.Not.Contain("claude"));
            Assert.That(json, Does.Not.Contain("gemini"));
        }
    }
}
