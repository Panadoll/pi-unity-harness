#if PI_UNITY_PIPELINE
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// Contract tests for run_tests mode normalization (PiUnityTestCoordinator.NormalizeMode) and
    /// the PlayMode test guard (PiUnityTestCoordinator.ShouldRejectPlaymodeRun).
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

        [Test]
        public void ShouldRejectPlaymodeRun_WhilePlaying_RejectsPlaymodeAndAll()
        {
            Assert.IsTrue(ShouldRejectPlaymodeRun("playmode", true), "播放中运行 PlayMode 测试应被拒绝");
            Assert.IsTrue(ShouldRejectPlaymodeRun("all", true), "播放中运行 all（含 PlayMode 段）应被拒绝");
        }

        [Test]
        public void ShouldRejectPlaymodeRun_NotPlaying_NeverRejects()
        {
            Assert.IsFalse(ShouldRejectPlaymodeRun("playmode", false), "非播放状态应放行");
            Assert.IsFalse(ShouldRejectPlaymodeRun("all", false), "非播放状态应放行");
            Assert.IsFalse(ShouldRejectPlaymodeRun("editor", false), "EditorMode 测试不受影响");
        }

        [Test]
        public void ShouldRejectPlaymodeRun_WhilePlaying_AllowsEditorMode()
        {
            Assert.IsFalse(ShouldRejectPlaymodeRun("editor", true), "播放中运行 EditorMode 测试不受影响");
        }

        [Test]
        public void StartRunTests_WhilePlaying_RejectsPlaymodeWithoutExecuting()
        {
            string errorJson = null;
            SetIsPlayingProvider(() => true);
            // 清理可能残留的 pending 状态，保证断言的是本次调用的行为
            SessionState.EraseString("PiUnityHarness_PendingTestRequestId");
            try
            {
                PiUnityTestCoordinator.StartRunTests(
                    "req-while-playing",
                    "{\"mode\":\"playmode\"}",
                    0,
                    (requestId, json) => errorJson = json);

                Assert.That(errorJson, Is.Not.Null, "播放中请求 PlayMode 测试应返回错误响应");
                Assert.That(errorJson, Does.Contain("\"ok\":false"), "守卫应返回错误响应");
                // 未设置 pending 请求，证明没有进入测试执行路径
                Assert.That(GetPendingTestRequestId(), Is.Empty, "守卫拒绝后不应设置 pending 请求");
            }
            finally
            {
                SetIsPlayingProvider(null);
                SessionState.EraseString("PiUnityHarness_PendingTestRequestId");
            }
        }

        private static void SetIsPlayingProvider(Func<bool> provider)
        {
            var field = typeof(PiUnityTestCoordinator).GetField(
                nameof(PiUnityTestCoordinator.IsPlayingProvider),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            field.SetValue(null, provider);
        }

        private static bool ShouldRejectPlaymodeRun(string mode, bool isPlaying)
        {
            var method = typeof(PiUnityTestCoordinator).GetMethod(
                nameof(ShouldRejectPlaymodeRun), BindingFlags.Static | BindingFlags.NonPublic);
            return (bool)method.Invoke(null, new object[] { mode, isPlaying });
        }

        private static string GetPendingTestRequestId()
        {
            return SessionState.GetString("PiUnityHarness_PendingTestRequestId", string.Empty);
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
