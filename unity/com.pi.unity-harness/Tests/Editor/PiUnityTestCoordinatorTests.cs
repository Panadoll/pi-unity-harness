#if PI_UNITY_PIPELINE
using System.Reflection;
using NUnit.Framework;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// run_tests 模式归一化逻辑（PiUnityTestCoordinator.NormalizeMode）的契约测试。
    /// 参数解析（TryParseParameters）已收敛到 PiUnityPipelineCommandExecutor，
    /// 由 PiUnityPipelineExecutorTests 覆盖，此处不再重复。
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
