using Pi.UnityHarness.Editor.Capabilities.UiTree;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests.UiTree
{
    /// <summary>
    /// Stale ref tests. Verifies that after epoch change, old refs
    /// return stale_ref error with guidance to re-snapshot.
    /// </summary>
    public sealed class UiTreeStaleRefTests
    {
        [Test]
        public void DescribeJson_StaleRef_ReturnsStaleRefError()
        {
            // Force a new epoch to make any existing ref stale
            int oldEpoch = UiTreeRefManager.CurrentEpoch;
            string fakeOldRef = "ref_" + (oldEpoch - 1) + "_1";

            string json = HarnessUiTree.DescribeJson(fakeOldRef);
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error_type\":\"stale_ref\""));
            Assert.That(json, Does.Contain("ListRootsJson"));
        }

        [Test]
        public void TextJson_StaleRef_ReturnsStaleRefError()
        {
            int oldEpoch = UiTreeRefManager.CurrentEpoch;
            string fakeOldRef = "ref_" + (oldEpoch - 1) + "_1";

            string json = HarnessUiTree.TextJson(fakeOldRef);
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error_type\":\"stale_ref\""));
        }

        [Test]
        public void ForceNewEpoch_InvalidatesCurrentRefs()
        {
            // Get a snapshot to create some refs
            HarnessUiTree.SnapshotJson(interactiveOnly: false, maxDepth: 1, limit: 5);

            // Get current epoch
            int currentEpoch = UiTreeRefManager.CurrentEpoch;
            string validRef = "ref_" + currentEpoch + "_1";

            // Force new epoch
            UiTreeRefManager.ForceNewEpoch();

            // Now the ref should be stale
            string json = HarnessUiTree.DescribeJson(validRef);
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error_type\":\"stale_ref\""));
        }

        [Test]
        public void DescribeJson_UnknownRef_ReturnsNotFound()
        {
            int currentEpoch = UiTreeRefManager.CurrentEpoch;
            // Use current epoch but a seq that doesn't exist
            string unknownRef = "ref_" + currentEpoch + "_99999";

            string json = HarnessUiTree.DescribeJson(unknownRef);
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error_type\":\"not_found\""));
        }

        [Test]
        public void DescribeJson_MalformedRef_ReturnsStaleRefError()
        {
            string json = HarnessUiTree.DescribeJson("garbage_ref");
            Assert.That(json, Does.Contain("\"status\":\"failed\""));
            Assert.That(json, Does.Contain("\"error_type\":\"stale_ref\""));
        }
    }
}
