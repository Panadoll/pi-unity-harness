using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class NativeAbiCheckTests
    {
        [Test]
        public void MatchingAbi_IsAccepted()
        {
            Assert.IsNull(PiUnityBridge.DescribeNativeAbiMismatch(3, 3));
        }

        [Test]
        public void DifferentAbi_IsRejectedWithBothVersionsAndRebuildSteps()
        {
            string error = PiUnityBridge.DescribeNativeAbiMismatch(2, 3);

            StringAssert.Contains("expects abi=3", error);
            StringAssert.Contains("reports abi=2", error);
            StringAssert.Contains("scripts/build-native.ps1", error);
        }

        [Test]
        public void MissingExport_IsRejectedAsPreAbiBuild()
        {
            string error = PiUnityBridge.DescribeNativeAbiMismatch(null, 1);

            StringAssert.Contains("does not export pi_unity_abi_version", error);
        }
    }
}
