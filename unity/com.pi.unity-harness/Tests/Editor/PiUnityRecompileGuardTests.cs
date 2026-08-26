using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace Pi.UnityHarness.Editor.Tests
{
    /// <summary>
    /// PiUnityRecompileGuard 契约测试：
    /// - 非播放状态直接发起编译；
    /// - PlayMode（含切换中）必须延迟编译、强制退出 PlayMode 后才继续；
    /// - 测试运行中返回 busy，不打断测试、不冒险 reload；
    /// - 等待期间超时返回 playmode_exit_timeout，不挂住 pipe 端请求；
    /// - 等待期间被重新进入 PlayMode 时再次退出并顺延超时。
    /// 全部状态经 SessionState 持久化，测试需在 finally 中恢复。
    /// </summary>
    internal sealed class PiUnityRecompileGuardTests
    {
        private static readonly string[] SessionKeys =
        {
            "PiUnityHarness_WaitPlaymodeCompileId",
            "PiUnityHarness_WaitPlaymodeDeadlineUtcTicks",
        };

        private readonly Dictionary<string, string> _savedSession = new Dictionary<string, string>();
        private readonly Dictionary<FieldInfo, object> _savedFields = new Dictionary<FieldInfo, object>();

        [SetUp]
        public void SaveGuardState()
        {
            _savedSession.Clear();
            foreach (var key in SessionKeys)
                _savedSession[key] = SessionState.GetString(key, string.Empty);

            _savedFields.Clear();
            foreach (var field in typeof(PiUnityRecompileGuard).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.IsLiteral)
                    continue;
                _savedFields[field] = field.GetValue(null);
            }
        }

        [TearDown]
        public void RestoreGuardState()
        {
            // 注销事件挂接，避免测试间重复注册（guard 内部动作）
            PiUnityRecompileGuard.UnregisterHandlersForTest();

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

        // 反射替身回调以绕过对真实编译协调器的依赖；断言编译启动时记录被调用的请求 id
        private string _compileStartedWith;
        private Action<string, bool, string, string> _lastOnComplete;

        private void InstallDeferredState(string requestId, string deadlineTicks, Action<string, bool, string, string> onComplete = null)
        {
            SetField("s_waitingPlayModeExit", true);
            _lastOnComplete = onComplete;
            SetField("s_onComplete", onComplete);
            SessionState.SetString("PiUnityHarness_WaitPlaymodeCompileId", requestId);
            SessionState.SetString("PiUnityHarness_WaitPlaymodeDeadlineUtcTicks", deadlineTicks);
        }

        private static void SetPlayProviders(bool isPlaying, bool willChange)
        {
            SetField("IsPlayingProvider", (Func<bool>)(() => isPlaying));
            SetField("WillChangePlaymodeProvider", (Func<bool>)(() => willChange));
        }

        private static void SetField(string name, object value)
        {
            typeof(PiUnityRecompileGuard).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .SetValue(null, value);
        }

        private static object GetField(string name)
        {
            return typeof(PiUnityRecompileGuard).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(null);
        }

        private static void InvokePrivate(string name, params object[] args)
        {
            typeof(PiUnityRecompileGuard).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, args);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            InvokePrivate("OnPlayModeStateChanged", state);
        }

        private static void OnEditorUpdate()
        {
            InvokePrivate("OnEditorUpdate");
        }

        private static string GetPendingId()
        {
            return SessionState.GetString("PiUnityHarness_WaitPlaymodeCompileId", string.Empty);
        }

        private static string GetDeadlineTicks()
        {
            return SessionState.GetString("PiUnityHarness_WaitPlaymodeDeadlineUtcTicks", string.Empty);
        }

        [Test]
        public void RequestRecompile_NotPlaying_StartsCompileImmediately()
        {
            string startedId = null;
            SetPlayProviders(false, false);
            SetField("IsTestRunInProgressProvider", (Func<bool>)(() => false));
            SetField("CompileStarter", (Action<string, Action<string, bool, string, string>>)((id, cb) => startedId = id));

            PiUnityRecompileGuard.RequestRecompile("req-direct", (id, s, t, e) => { });

            Assert.That(startedId, Is.EqualTo("req-direct"), "非播放状态应直接发起编译");
            Assert.That(GetPendingId(), Is.Empty, "非播放状态不应设置等待中的 pending");
        }

        [Test]
        public void RequestRecompile_WhilePlaying_DefersCompileAndExitsPlayMode()
        {
            string startedId = null;
            bool exitedPlayMode = false;
            string deferredOnCompleteResult = null;
            SetPlayProviders(true, true);
            SetField("IsTestRunInProgressProvider", (Func<bool>)(() => false));
            SetField("ExitPlaymodeAction", (Action)(() => exitedPlayMode = true));
            SetField("CompileStarter", (Action<string, Action<string, bool, string, string>>)((id, cb) => startedId = id));

            PiUnityRecompileGuard.RequestRecompile("req-deferred", (id, success, resultText, error) => deferredOnCompleteResult = resultText);

            Assert.That(startedId, Is.Null, "播放中不应立即发起编译");
            Assert.That(exitedPlayMode, Is.True, "播放中应强制退出 PlayMode");
            Assert.That(GetPendingId(), Is.EqualTo("req-deferred"), "应持久化等待中的请求 id");
            Assert.That(GetDeadlineTicks(), Is.Not.Empty, "应持久化超时截止时间");
            Assert.That(deferredOnCompleteResult, Is.Null, "延迟请求的完成回调不应提前触发");
        }

        [Test]
        public void RequestRecompile_WithTestRunInProgress_ReturnsBusy()
        {
            string startedId = null;
            string busyErrorType = null;
            SetPlayProviders(true, true);
            SetField("IsTestRunInProgressProvider", (Func<bool>)(() => true));
            SetField("CompileStarter", (Action<string, Action<string, bool, string, string>>)((id, cb) => startedId = id));

            PiUnityRecompileGuard.RequestRecompile("req-while-tests", (id, success, resultText, error) => busyErrorType = resultText);

            Assert.That(startedId, Is.Null, "测试运行中不应发起编译");
            Assert.That(busyErrorType, Is.EqualTo("busy"), "测试运行中应返回 busy");
            Assert.That(GetPendingId(), Is.Empty, "测试运行中拒绝后不应设置 pending");
        }

        [Test]
        public void RequestRecompile_WhileQueued_ReturnsBusy()
        {
            string busyErrorType = null;
            SetPlayProviders(true, true);
            SetField("IsTestRunInProgressProvider", (Func<bool>)(() => false));
            InstallDeferredState("req-first", "0");

            PiUnityRecompileGuard.RequestRecompile("req-second", (id, success, resultText, error) => busyErrorType = resultText);

            Assert.That(busyErrorType, Is.EqualTo("busy"), "已有排队请求时新请求应返回 busy");
            Assert.That(GetPendingId(), Is.EqualTo("req-first"), "排队中的原始请求应保持不变");
        }

        [Test]
        public void ResumeAfterReload_NoPending_IsNoOp()
        {
            bool fallbackCalled = false;
            SessionState.EraseString("PiUnityHarness_WaitPlaymodeCompileId");

            PiUnityRecompileGuard.ResumeAfterReload((id, s, t, e) => fallbackCalled = true);

            Assert.That(fallbackCalled, Is.False, "无 pending 时恢复应为空操作");
            Assert.That((bool)GetField("s_waitingPlayModeExit"), Is.False, "无 pending 时不应进入等待状态");
        }

        [Test]
        public void DeferredRequest_TimedOut_ReturnsPlaymodeExitTimeoutAndClearsState()
        {
            string errorType = null;
            string errorText = null;
            SetPlayProviders(true, true);
            // 已过期的截止时间
            InstallDeferredState("req-timeout", "1", (id, success, resultText, error) =>
            {
                errorType = resultText;
                errorText = error;
            });
            SetField("UtcNowProvider", (Func<DateTime>)(() => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

            OnEditorUpdate();

            Assert.That(errorType, Is.EqualTo("playmode_exit_timeout"), "超时应返回 playmode_exit_timeout");
            Assert.That(errorText, Does.Contain("timed out waiting for PlayMode"), "超时应给出可读错误信息");
            Assert.That(GetPendingId(), Is.Empty, "超时后应清理 pending");
            Assert.That(GetDeadlineTicks(), Is.Empty, "超时后应清理截止时间");
        }

        [Test]
        public void ContinueCompileIfSafe_AfterPlayModeExit_StartsCompileAndClearsState()
        {
            string startedId = null;
            SetPlayProviders(false, false);
            InstallDeferredState("req-continue", "9999999999999999999",
                (id, success, resultText, error) => { });
            SetField("CompileStarter", (Action<string, Action<string, bool, string, string>>)((id, cb) => startedId = id));
            // 测试运行场景下 isCompiling 为 false，可直接进入编译路径
            Assume.That(EditorApplication.isCompiling, Is.False, "测试应在非编译期间运行");

            InvokePrivate("ContinueCompileIfSafe");

            Assert.That(startedId, Is.EqualTo("req-continue"), "PlayMode 退出后应发起编译");
            Assert.That(GetPendingId(), Is.Empty, "发起编译后应清理 pending");
            Assert.That((bool)GetField("s_waitingPlayModeExit"), Is.False, "发起编译后应退出等待状态");
        }

        [Test]
        public void DeferredRequest_ReenteredPlayMode_ExitsAgainAndExtendsDeadline()
        {
            int exitCount = 0;
            SetField("ExitPlaymodeAction", (Action)(() => exitCount++));
            var fixedNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SetField("UtcNowProvider", (Func<DateTime>)(() => fixedNow));
            InstallDeferredState("req-reenter", "0");

            OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);

            Assert.That(exitCount, Is.EqualTo(1), "等待期间被重新进入 PlayMode 应再次强制退出");
            Assert.That(GetPendingId(), Is.EqualTo("req-reenter"), "重新进入后请求应继续等待");
            // 截止时间应顺延到（当前时间 + 60s，与守卫默认超时一致）
            var expected = fixedNow.AddSeconds(60.0d);
            Assert.That(long.Parse(GetDeadlineTicks()), Is.GreaterThanOrEqualTo(expected.Ticks), "重新进入后应顺延超时截止时间");
        }
    }
}