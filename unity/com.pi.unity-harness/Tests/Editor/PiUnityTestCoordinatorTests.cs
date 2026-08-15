#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
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
        // PiUnityTestCoordinator 的全部静态字段与 SessionState 键。测试调用 StartRunTests
        // 会污染真实 run_tests 协调器的状态（s_completeJson / pending 等），必须在 finally 中恢复。
        private static readonly string[] SessionKeys =
        {
            "PiUnityHarness_PendingTestRequestId",
            "PiUnityHarness_OriginalTestParametersJson",
            "PiUnityHarness_CurrentTestSegment",
            "PiUnityHarness_EditorTestSegmentResultJson",
            "PiUnityHarness_TestDeadlineUtcTicks",
        };

        private readonly Dictionary<string, string> _savedSession = new Dictionary<string, string>();
        private readonly Dictionary<FieldInfo, object> _savedFields = new Dictionary<FieldInfo, object>();

        [SetUp]
        public void SaveCoordinatorState()
        {
            _savedSession.Clear();
            foreach (var key in SessionKeys)
                _savedSession[key] = SessionState.GetString(key, string.Empty);

            _savedFields.Clear();
            foreach (var field in typeof(PiUnityTestCoordinator).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.IsLiteral)
                    continue; // const 字段不可写，跳过
                _savedFields[field] = field.GetValue(null);
            }
        }

        [TearDown]
        public void RestoreCoordinatorState()
        {
            foreach (var pair in _savedFields)
                pair.Key.SetValue(null, pair.Value);
            foreach (var pair in _savedSession)
            {
                if (string.IsNullOrEmpty(pair.Value))
                    SessionState.EraseString(pair.Key);
                else
                    SessionState.SetString(pair.Key, pair.Value);
            }
        }
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
            SetPipelineTestRunRunningProvider(() => false);
            // 真实 run_tests 协调器持有 pending；临时清空以验证本请求的守卫行为（TearDown 恢复原值）
            SessionState.EraseString("PiUnityHarness_PendingTestRequestId");

            PiUnityTestCoordinator.StartRunTests(
                "req-while-playing",
                "{\"mode\":\"playmode\"}",
                0,
                (requestId, json) => errorJson = json);

            Assert.That(errorJson, Is.Not.Null, "播放中请求 PlayMode 测试应返回错误响应");
            Assert.That(errorJson, Does.Contain("\"ok\":false"), "守卫应返回错误响应");
            Assert.That(errorJson, Does.Contain("playmode_active"), "守卫应返回 playmode_active 错误类型");
            // 未设置本次请求的 pending，证明没有进入测试执行路径（外部协调器的 pending 保持原值）
            Assert.That(GetPendingTestRequestId(), Is.Not.EqualTo("req-while-playing"), "守卫拒绝后不应设置本次请求的 pending");
        }

        [Test]
        public void StartRunTests_DirtySceneWithAbort_ReturnsErrorWithoutExecuting()
        {
            string errorJson = null;
            SetPipelineTestRunRunningProvider(() => false);
            SessionState.EraseString("PiUnityHarness_PendingTestRequestId");

            // 前置：构造脏场景（Single 模式替换当前 untitled 场景后再标记 dirty）
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            PiUnityTestCoordinator.StartRunTests(
                "req-dirty-abort",
                "{\"mode\":\"editor\"}",
                0,
                (requestId, json) => errorJson = json);

            Assert.That(errorJson, Is.Not.Null, "脏场景 + 默认 abort 应返回错误响应");
            Assert.That(errorJson, Does.Contain("\"ok\":false"), "策略拒绝应返回错误响应");
            Assert.That(errorJson, Does.Contain("dirtyAction"), "错误信息应提示 dirtyAction 选项");
            Assert.That(errorJson, Does.Contain("dirty_scene"), "策略拒绝应返回 dirty_scene 错误类型");
            Assert.That(GetPendingTestRequestId(), Is.Not.EqualTo("req-dirty-abort"), "策略拒绝后不应设置本次请求的 pending");
        }

        [Test]
        public void StartRunTests_DirtySceneWithDiscard_ProceedsWithoutExecuting()
        {
            string errorJson = null;
            bool invokerCalled = false;
            SetPipelineTestRunRunningProvider(() => false);
            SessionState.EraseString("PiUnityHarness_PendingTestRequestId");
            typeof(PiUnityTestCoordinator).GetField(
                nameof(PiUnityTestCoordinator.RunTestsInvoker),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .SetValue(null, (System.Func<string, string, string, bool, int, System.Threading.Tasks.Task<Unity.Pipeline.TestExecutionResponse>>)(
                (mode, filter, filterType, includeExplicit, timeout) =>
                {
                    invokerCalled = true;
                    var response = new Unity.Pipeline.TestExecutionResponse { Success = true };
                    return System.Threading.Tasks.Task.FromResult(response);
                }));

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            PiUnityTestCoordinator.StartRunTests(
                "req-dirty-discard",
                "{\"mode\":\"editor\",\"dirty_action\":\"discard\"}",
                0,
                (requestId, json) => errorJson = json);

            // discard 策略放行：应进入执行路径（invoker 被调用），且不因脏场景报错
            Assert.That(invokerCalled, Is.True, "discard 策略应放行到测试执行路径");
            Assert.That(errorJson, Is.Null.Or.Not.Contain("dirtyAction"), "discard 策略不应因脏场景报错");
        }

        [Test]
        public void StartRunTests_UntitledDirtySceneWithSave_ReturnsError()
        {
            string errorJson = null;
            SetPipelineTestRunRunningProvider(() => false);
            SessionState.EraseString("PiUnityHarness_PendingTestRequestId");

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Assert.That(string.IsNullOrEmpty(scene.path), Is.True, "前置条件：场景应未保存（untitled）");

            PiUnityTestCoordinator.StartRunTests(
                "req-dirty-save-untitled",
                "{\"mode\":\"editor\",\"dirty_action\":\"save\"}",
                0,
                (requestId, json) => errorJson = json);

            Assert.That(errorJson, Is.Not.Null, "untitled 脏场景 + save 应报错");
            Assert.That(errorJson, Does.Contain("\"ok\":false"), "save 失败应按错误响应返回");
            Assert.That(errorJson, Does.Contain("dirty_scene"), "save 失败应返回 dirty_scene 错误类型");
            Assert.That(GetPendingTestRequestId(), Is.Not.EqualTo("req-dirty-save-untitled"), "save 失败后不应设置本次请求的 pending");
        }

        private static Func<bool> SetPipelineTestRunRunningProvider(Func<bool> provider)
        {
            var field = typeof(PiUnityTestCoordinator).GetField(
                nameof(PiUnityTestCoordinator.PipelineTestRunRunningProvider),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var previous = (Func<bool>)field.GetValue(null);
            field.SetValue(null, provider);
            return previous;
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
