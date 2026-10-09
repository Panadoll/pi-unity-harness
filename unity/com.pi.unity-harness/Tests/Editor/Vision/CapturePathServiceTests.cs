using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Vision;

namespace Pi.UnityHarness.Editor.Tests.Vision
{
    public sealed class CapturePathServiceTests
    {
        private static readonly DateTime Stamp = new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc);

        [Test]
        public void RelativePath_HarnessUsesProjectRoot_LegacyCameraUsesCwd()
        {
            string root = Path.Combine(Path.GetTempPath(), "capture-root-" + Guid.NewGuid().ToString("N"));
            string harness = CapturePathService.ResolveWithoutCreate(CapturePathProfile.Harness, "shots/a.png", root, Stamp);
            string camera = CapturePathService.ResolveWithoutCreate(CapturePathProfile.LegacyCamera, "shots/a.png", root, Stamp);
            string observe = CapturePathService.ResolveWithoutCreate(CapturePathProfile.PlaytestObserve, "shots/a.png", root, Stamp);

            Assert.That(harness, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "shots", "a.png"))));
            Assert.That(observe, Is.EqualTo(harness));
            Assert.That(camera, Is.EqualTo(Path.GetFullPath("shots/a.png")));
            Assert.That(camera, Is.Not.EqualTo(harness));
        }

        [Test]
        public void Resolve_CreatesParentOnlyWhenAsked_AndDoesNotWriteFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "capture-mkdir-" + Guid.NewGuid().ToString("N"));
            try
            {
                string untouched = CapturePathService.ResolveWithoutCreate(
                    CapturePathProfile.LegacyCamera, null, root, Stamp);
                Assert.That(Directory.Exists(Path.GetDirectoryName(untouched)), Is.False);

                string created = CapturePathService.Resolve(
                    CapturePathProfile.Harness, "nested/shot.png", root, Stamp, true);
                Assert.That(Directory.Exists(Path.GetDirectoryName(created)), Is.True);
                Assert.That(File.Exists(created), Is.False);
                Assert.That(created, Does.StartWith(Path.GetFullPath(root)));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Test]
        public void CaptureJson_SyncGameAndInvalidMode_FailBeforePathSideEffect()
        {
            string root = Path.Combine(Path.GetTempPath(), "capture-early-" + Guid.NewGuid().ToString("N"));
            string gamePath = Path.Combine(root, "should-not-exist", "game.png");
            string badPath = Path.Combine(root, "should-not-exist", "bad.png");
            try
            {
                string game = HarnessVision.CaptureJson("game", gamePath, 0, 0);
                string invalid = HarnessVision.CaptureJson("window", badPath, 8, 9000);

                JObject gameJson = JObject.Parse(game);
                JObject invalidJson = JObject.Parse(invalid);
                Assert.That((string)gameJson["status"], Is.EqualTo("failed"));
                Assert.That((string)gameJson["error_type"], Is.EqualTo("not_supported"));
                Assert.That(gameJson["path"], Is.Null);
                Assert.That((string)invalidJson["status"], Is.EqualTo("failed"));
                Assert.That((string)invalidJson["error_type"], Is.EqualTo("usage"));
                Assert.That(invalidJson["path"], Is.Null);
                Assert.That(Directory.Exists(Path.Combine(root, "should-not-exist")), Is.False);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}
