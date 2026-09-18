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
        public void AnalyzeImageJson_DefaultProvider_ReturnsPartial()
        {
            // This test verifies provider behavior without rendering a SceneView camera.
            string path = Path.Combine(Path.GetTempPath(), "harness-vision-unavailable-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, new byte[]
            {
                137, 80, 78, 71, 13, 10, 26, 10,
                0, 0, 0, 13, 73, 72, 82,
                0, 0, 1, 0, 0, 0, 1, 0
            });

            try
            {
                string json = HarnessVision.AnalyzeImageJson(
                    "Find the Start button.", path, null, null);

                Assert.That(json, Does.Contain("\"status\":\"partial\""));
                Assert.That(json, Does.Contain("\"status\":\"unavailable\""));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
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
    }
}
