#if PI_UNITY_PIPELINE
using System.Reflection;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// Contract tests for run_tests mode normalization (PiUnityTestCoordinator.NormalizeMode).
    /// Parameter parsing (TryParseParameters) has been consolidated into PiUnityPipelineCommandExecutor,
    /// covered by PiUnityPipelineExecutorTests; not repeated here.
    /// </summary>
    internal sealed class PiUnityTestCoordinatorTests
    {
        [Test]
        public void NormalizeMode_AcceptsSupportedModesAndRejectsInvalidMode()
        {
            Assert.AreEqual("editor", NormalizeMode("editor"));
            Assert.AreEqual("editor", NormalizeMode("editmode"));
            Assert.AreEqual("playmode", NormalizeMode("play"));
            Assert.AreEqual("playmode", NormalizeMode("playmode"));
            Assert.AreEqual("all", NormalizeMode("all"));
            Assert.AreEqual("all", NormalizeMode(""));
            Assert.IsNull(NormalizeMode("invalid"));
        }

        private static string NormalizeMode(string mode)
        {
            return (string)typeof(PiUnityTestCoordinator)
                .GetMethod(nameof(NormalizeMode), BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { mode });
        }
    }
}
#endif
